Option Explicit
Dim shell, files, root, script, command, app, stamp, signature, arg, result, output, runtime
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
root = files.GetParentFolderName(WScript.ScriptFullName)
script = files.BuildPath(root, "scripts\start-development.ps1")
app = files.BuildPath(root, "artifacts\development\Release\net10.0-windows\ClassroomCaption.exe")
stamp = files.BuildPath(root, "artifacts\development-inputs.txt")
runtime = files.BuildPath(root, ".tools\dotnet")
shell.Environment("PROCESS")("DOTNET_ROOT") = runtime
shell.Environment("PROCESS")("DOTNET_ROOT_X64") = runtime
shell.CurrentDirectory = root

signature = "development-launcher-v2" & vbCrLf
ScanFolder files.BuildPath(root, "src")
ScanFolder files.BuildPath(root, "native")
AddFile files.BuildPath(root, "Directory.Build.props")
AddFile files.BuildPath(root, "global.json")
AddFile files.BuildPath(root, "NuGet.Config")
AddFile files.BuildPath(root, "THIRD-PARTY-NOTICES.txt")
AddFile script
AddFile WScript.ScriptFullName

If Not IsCurrent() Then
    command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " & Quote(script) & " -BuildOnly"
    result = shell.Run(command, 0, True)
    If result <> 0 And result <> 3 Then WScript.Quit result
    ' Exit 3 means the old build is still running: activate it, and leave the
    ' stamp stale so the next launch after exit will compile the pending changes.
    If result = 0 Then
        Set output = files.CreateTextFile(stamp, True, True)
        output.Write signature
        output.Close
    End If
End If
command = Quote(app)
For Each arg In WScript.Arguments
    command = command & " " & Quote(arg)
Next
shell.Run command, 1, False

Function Quote(value)
    Quote = Chr(34) & Replace(value, Chr(34), Chr(34) & Chr(34)) & Chr(34)
End Function

Sub AddFile(path)
    Dim item
    If files.FileExists(path) Then
        Set item = files.GetFile(path)
        signature = signature & path & "|" & CStr(item.Size) & "|" & CStr(CDbl(item.DateLastModified)) & vbCrLf
    Else
        signature = signature & path & "|missing" & vbCrLf
    End If
End Sub

Sub ScanFolder(path)
    Dim folder, item, child, name
    If Not files.FolderExists(path) Then Exit Sub
    Set folder = files.GetFolder(path)
    For Each item In folder.Files
        AddFile item.Path
    Next
    For Each child In folder.SubFolders
        name = LCase(child.Name)
        If name <> "bin" And name <> "obj" And name <> "artifacts" And (child.Attributes And 1024) = 0 Then ScanFolder child.Path
    Next
End Sub

Function IsCurrent()
    Dim input, dependency
    IsCurrent = False
    If Not files.FileExists(app) Or Not files.FileExists(stamp) Then Exit Function
    For Each dependency In Array("ClassroomCaption.Core.dll", "ClassroomCaption.dll", "ClassroomCaption.runtimeconfig.json", "ClassroomCaption.deps.json", "rnnoise.dll", "WpfMath.dll", "XamlMath.Shared.dll")
        If Not files.FileExists(files.BuildPath(files.GetParentFolderName(app), dependency)) Then Exit Function
    Next
    Set input = files.OpenTextFile(stamp, 1, False, -1)
    IsCurrent = (input.ReadAll = signature)
    input.Close
End Function
