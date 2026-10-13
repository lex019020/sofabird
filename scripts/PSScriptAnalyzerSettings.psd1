# PSScriptAnalyzer settings for scripts/ (CI job "Scripts"). Every excluded rule says why.
@{
    Severity     = @('Error', 'Warning')
    ExcludeRules = @(
        # The scripts are interactive console tools: coloured progress lines go to the host on purpose.
        'PSAvoidUsingWriteHost'
    )
}
