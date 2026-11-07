# iConnect security policy

## Report a vulnerability privately

Use **Report a vulnerability** in the repository’s **Security and quality** tab when private vulnerability reporting is available. Otherwise, use a private channel agreed with a maintainer. A public issue can request a private contact without including the vulnerability details. [GitHub private reporting instructions](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/report-privately)

Include the affected Windows, Android or coordinator version, the environment, steps to reproduce with generated data, and the access or effect observed. Use a minimal fixture. Keep device keys, pairing codes, access tokens, private addresses and personal files out of the report.

The maintainer should confirm receipt privately, assess the affected versions and scope, and coordinate disclosure with the reporter. Reproduce the issue in a disposable environment, verify the fix, and record the affected and fixed versions in the advisory. For a distributed build, attach the mitigation or verified update path to the advisory. [GitHub advisory workflow](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/fix-reported-vulnerabilities/manage-vulnerability-reports)

## Release reviews

iConnect is in development. Use the [release security procedure](docs/development/release-security.md) to identify the exact package under review, check published dependency advisories, and record the remaining security review evidence.
