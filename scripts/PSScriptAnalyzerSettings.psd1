# PSScriptAnalyzer settings for scripts/ (CI job "Scripts"). Every excluded rule says why.
@{
    Severity     = @('Error', 'Warning')
    ExcludeRules = @(
        # The scripts are interactive console tools: coloured progress lines go to the host on purpose.
        'PSAvoidUsingWriteHost',
        # Script parameters are read by the script's own functions (script scope), which this rule does not follow.
        'PSReviewUnusedParameter',
        # Internal helpers that act on a set are named after it (Get-AllowedDevices, Add-FirewallRules).
        'PSUseSingularNouns',
        # Internal helpers, not user-facing cmdlets: the scripts themselves are the commands (no -WhatIf).
        'PSUseShouldProcessForStateChangingFunctions'
    )
}
