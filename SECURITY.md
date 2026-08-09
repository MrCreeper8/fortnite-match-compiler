# Security policy

## Supported versions

Security fixes are made against the latest published release and the default branch. Please reproduce an issue with the latest version before reporting it when practical.

## Report a vulnerability

Do not disclose a suspected vulnerability in a public issue, discussion, pull request, video, or log attachment.

Use GitHub's private vulnerability reporting option from the repository's **Security** tab. If that option is unavailable, open a public issue that asks the maintainer to establish private contact, but do not include technical details until a private channel is available.

Include only the information needed to reproduce and assess the issue:

- The affected version and Windows version.
- A concise description of the impact.
- Reproduction steps or a minimal proof of concept.
- Any suggested mitigation.

Logs can contain local paths and NVIDIA filenames. Redact usernames, folder names, filenames, and gameplay information that are not essential to the report. Never attach original recordings unless the maintainer explicitly requests a private sample and you are comfortable sharing it.

## Scope

Fortnite Match Compiler processes user-selected local files and starts a user-provided FFmpeg installation. Vulnerabilities in FFmpeg itself should also be reported to the FFmpeg project. General bugs and feature requests that do not have a security impact belong in the public issue tracker.
