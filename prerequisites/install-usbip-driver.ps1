<#
.SYNOPSIS
    OPT-IN install of the pinned usbip-win2 USB/IP kernel driver (needed on the HOST for USB forwarding).
.DESCRIPTION
    Not part of any default or unattended run. install-prerequisites.ps1 calls this only when you pass
    -WithUsbForwarding.

    Pinned release: vadimgrn/usbip-win2 v.0.9.8.1, asset USBip-0.9.8.1-x64.exe, SHA-256 checked.

    RULE: usbip-win2 is used ONLY while its kernel drivers are signed by Microsoft. The script checks the
    installer's hash and signature before running it, then checks the installed drivers. If any driver is
    not Microsoft-signed it stops, enables nothing and exits 2. It never turns on test-signing mode and
    refuses to run while test-signing is on.

    The script NEVER restarts the machine. The driver needs a reboot before it works; that is your call.
.PARAMETER WhatIf
    Print every step and do nothing: no download, no install, no system query.
.NOTES
    Exit codes: 0 ok (installed or already present), 1 refused or failed, 2 driver not Microsoft-signed.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\usbip-driver-lib.ps1')

$result = Invoke-UsbipDriverInstall -DryRun:([bool]$WhatIfPreference)
exit $result.ExitCode
