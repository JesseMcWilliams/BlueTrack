@{
    # PSAvoidUsingWriteHost is excluded deliberately, not overlooked: every
    # script under Deploy/ is an interactive, console-facing installer (or a
    # module supporting one), not a reusable library meant to be silently
    # piped/redirected. Write-Host is the correct tool for colored progress
    # headers and status lines here; Write-Output would pollute functions
    # that also need to return a clean value (e.g.
    # BlueTrack.Smoke.psm1's Test-BlueTrackDeployment, which prints
    # human-readable status *and* returns a [bool]).
    ExcludeRules = @('PSAvoidUsingWriteHost')
}
