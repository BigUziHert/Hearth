# Hearth 1.0.13

Fixes the false firewall failure after approving Windows. Windows had created the correct rule, but Hearth looked up its internal identifier through an API that uses display names. Hearth now verifies the actual executable, port and allowed network scope across the existing rules, including when several rules share a display name.

The rule you already approved is reused, so this fix does not need another administrator prompt. Existing host data, keys and player files are preserved. Helper failures and unsuccessful verification now have distinct error messages instead of saying that you did not approve Windows.
