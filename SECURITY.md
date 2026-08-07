# Security Policy

## Supported versions

Security fixes are applied to the latest commit on `main`. No older release line is currently maintained.

## Reporting a vulnerability

Please do not disclose a suspected vulnerability in a public issue.

Use **Security → Report a vulnerability** in this GitHub repository to submit details privately. If private vulnerability reporting is unavailable, contact the maintainer through the contact method shown on the [`qivarnq3g` GitHub profile](https://github.com/qivarnq3g) and request a private channel without including exploit details.

Include the affected component, reproduction steps, impact, and any suggested mitigation. You should receive an acknowledgement within seven days. Status updates will be provided as the report is validated and addressed.

## Security boundaries

Collaborative Whiteboard LAN is intended for trusted local networks. It currently does not provide transport encryption or strong user identity authentication. Room passwords reduce accidental access but are not a substitute for TLS or an identity provider. Do not expose the server directly to the public internet or reuse a sensitive password.
