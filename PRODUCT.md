# Product

<!-- impeccable:product-schema 1 -->

## Platform

Windows desktop (WPF).

## Users

People following a Russian-language class who need Chinese subtitles while listening. The current project is used for classroom and ship-engineering terminology.

## Product Purpose

Capture speech from a selected microphone, recognize Russian in real time, and show the original and Chinese translation in a readable main window and movable overlay.

## Operating Context

The user starts and stops a class, chooses an input device and translation pace, and may view or export the text afterward. The overlay can remain above teaching material. Network, microphone, translation, saving, and billing states must remain understandable during the session.

## Capabilities and Constraints

- Existing WPF application using Aliyun LiveTranslate for both source captions and Chinese translation; keep the existing logic, settings, transcript history, overlay controls, and data compatibility.
- Save text, not audio. API keys are directly displayed and stored as plaintext locally by existing user choice; disclose this where credentials are edited.
- Distinguish estimates, account balance changes, and provider bills. Never imply that an estimate is a final charge or that missing usage is zero.
- Preserve usable behavior at the existing minimum window size and provide clear keyboard focus and control names.
- The user requests concise UI text and chose a professional dark visual direction for this redesign.

## Evidence on Hand

- Product and implementation details: `README.md` and `src/ClassroomCaption.App/`.
- Demonstration text is synthetic and must remain clearly identified as a demo.

## Product Principles

- Put current subtitles and the class state before configuration and diagnostics.
- Make primary actions and recovery from errors easy to find.
- Keep necessary cost, credential, and recording information honest and accessible.
- Use restraint without hiding the meaning of a control or state.
