"""Build curated course terminology with verifiable PPT/transcript references.
No machine-translated classroom text is treated as an authoritative dictionary.
"""
import json
import pathlib
import re
import unicodedata
import zipfile
import xml.etree.ElementTree as ET

root = pathlib.Path(r"D:\桌面文件夹\学习\大学课程\过考试\船舶摇摆理论")
project = pathlib.Path(__file__).resolve().parents[1]

def norm(text):
    return re.sub(r"\s+", " ", unicodedata.normalize("NFKC", text).casefold().replace("ё", "е")).strip()

def contains(text, term):
    return re.search(r"(?<![а-яa-z])" + re.escape(norm(term)) + r"(?![а-яa-z])", text) is not None

sources = []
decks = []
for folder in sorted(root.iterdir()):
    if not folder.is_dir() or not re.fullmatch(r"\d+[ap]", folder.name):
        continue
    for path in sorted(folder.glob("*.pptx")):
        decks.append(str(path.relative_to(root)))
        with zipfile.ZipFile(path) as archive:
            for name in archive.namelist():
                match = re.fullmatch(r"ppt/slides/slide(\d+)\.xml", name)
                if not match:
                    continue
                tree = ET.fromstring(archive.read(name))
                text = " ".join("".join(t.text or "" for t in para.iter() if t.tag.rsplit("}", 1)[-1] == "t")
                                for para in tree.iter() if para.tag.rsplit("}", 1)[-1] == "p")
                sources.append(({"file": str(path.relative_to(root)), "slide": int(match[1])}, norm(text)))
slide_count = len(sources)

legacy = [project / folder / "sessions/课堂-20261104-101536-9e1fc5.jsonl" for folder in ("历史/data", "历史/data0")]
history = [next((path for path in legacy if path.is_file()), legacy[0])]
history.extend(sorted((project / "历史/课堂字幕/data/sessions").glob("课堂*.jsonl")))
warnings = []
segment_count = 0
history_files = []
for path in history:
    if not path.is_file():
        warnings.append("Absent transcript: " + str(path.relative_to(project)))
        continue
    history_files.append(str(path.relative_to(project)))
    for number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
        if not line.strip():
            continue
        try:
            row = json.loads(line)
        except json.JSONDecodeError:
            warnings.append(str(path.relative_to(project)) + ": invalid JSON at line " + str(number))
            continue
        if row.get("type") != "segment" or not row.get("confirmed", False):
            continue
        russian = row.get("Russian", "")
        if not russian.strip():
            continue
        segment_count += 1
        sources.append(({"file": str(path.relative_to(project)), "slide": 0, "line": number}, norm(russian)))

candidates = []
for line in (project / "scripts/course-term-candidates.txt").read_text(encoding="utf-8").splitlines():
    if line.strip():
        source, target = line.split(" = ", 1)
        candidates.append({"source": source, "target": target})
for source, target in [("омега", "ω"), ("ню", "ν"), ("зета", "ζ")]:
    candidates.append({"source": source, "target": target})
for path in sorted((project / "scripts").glob("course-term-additions-*.json")):
    candidates.extend(json.loads(path.read_text(encoding="utf-8")))

terms = []
missing = []
seen = set()
for candidate in candidates:
    source, target = candidate["source"], candidate["target"]
    key = source.casefold()
    if key in seen:
        raise ValueError("Duplicate candidate: " + source)
    seen.add(key)
    forms = [source] + candidate.get("evidenceForms", [])
    references = []
    for reference, text in sources:
        matched = next((form for form in forms if contains(text, form)), None)
        if matched:
            references.append({**reference, "matchedText": matched})
    if not references:
        missing.append(source)
        continue
    terms.append({"source": source, "target": target, "references": references})
if len(terms) > 1000:
    raise ValueError("LiveTranslate terminology limit exceeded")
output = project / "src/ClassroomCaption.Core/Assets/ship-motion-terms.json"
old = json.loads(output.read_text(encoding="utf-8")) if output.exists() else []
lost = sorted({term["source"] for term in old} - {term["source"] for term in terms})
if lost:
    raise ValueError("Existing terminology would be lost; verify evidence first: " + ", ".join(lost))
previous = {term["source"]: term["target"] for term in old}
changed = [term["source"] for term in terms if term["source"] in previous and term["target"] != previous[term["source"]]]
if changed:
    raise ValueError("Existing translations changed; review explicitly before rebuilding: " + ", ".join(changed))
output.write_text(json.dumps(terms, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

report = ["# 船舶摇荡课程内置术语", "",
          f"读取 {len(decks)} 份课件、{slide_count} 页及 {len(history_files)} 份真实课堂记录中的 {segment_count} 条已确认俄语片段；采用 {len(terms)} 条人工审核译法。演示、暂译及明显 ASR 错拼不作为术语来源。", "",
          "来源列使用 PPT 物理页码或 JSONL 行号。俄语原文只用于确认术语出现过，中文译法经人工审查，不直接抄录模型译文。规范词形允许用明确的变格形式作证据；相应 matchedText 保存在内置资源中。公式及重复的公式备用文本不加入术语表。", "",
          "| 俄语 | 中文 | 来源 |", "|---|---|---|"]
for term in terms:
    refs = []
    for ref in term["references"][:3]:
        suffix = " 第" + str(ref["slide"]) + "页" if ref["slide"] else " 第" + str(ref["line"]) + "行"
        refs.append(ref["file"] + suffix)
    report.append("| " + term["source"] + " | " + term["target"] + " | " + "；".join(refs) + " |")
if missing:
    report.extend(["", "未找到可靠文字证据的候选未采用：" + "、".join(missing) + "。"])
if warnings:
    report.extend(["", "读取提示："] + warnings)
(project / "docs/课程术语来源.md").write_text("\n".join(report) + "\n", encoding="utf-8")
print(json.dumps({"decks": len(decks), "slides": slide_count, "classroomFiles": len(history_files),
                  "confirmedSegments": segment_count, "terms": len(terms), "added": len(terms) - len(old),
                  "unmatchedCandidates": missing, "warnings": warnings}, ensure_ascii=False))
