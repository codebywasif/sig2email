namespace Sig2Email.Api;

/// <summary>Exchange Online PowerShell that wires a tenant's outbound mail through the regional SMTP nodes.</summary>
public static class ProvisioningScript
{
    private const string Outbound = "Sig2Email Outbound";
    private const string Inbound = "Sig2Email Inbound";
    private const string RuleName = "Sig2Email - route outbound mail for signatures";

    public static string Build(string smtpHost, string? scopeGroupMail)
    {
        var scope = scopeGroupMail == null ? "" : $" `\n    -FromMemberOf '{scopeGroupMail.Replace("'", "''")}'";
        return $$"""
            # Sig2Email – Exchange Online mail-flow provisioning
            # Run in PowerShell 7 as an Exchange Administrator. Requires: Install-Module ExchangeOnlineManagement
            # Scope: {{(scopeGroupMail == null ? "all senders in the organisation" : "members of " + scopeGroupMail)}}

            $ErrorActionPreference = 'Stop'
            Connect-ExchangeOnline

            $smartHost = '{{smtpHost}}'

            # 1. Outbound connector: Exchange Online -> Sig2Email (only used when the transport rule selects it)
            if (-not (Get-OutboundConnector -Identity '{{Outbound}}' -ErrorAction SilentlyContinue)) {
                New-OutboundConnector -Name '{{Outbound}}' `
                    -ConnectorType OnPremises `
                    -IsTransportRuleScoped $true `
                    -UseMXRecord $false `
                    -SmartHosts $smartHost `
                    -TlsSettings DomainValidation `
                    -TlsDomain $smartHost `
                    -CloudServicesMailEnabled $true `
                    -Comment 'Routes outbound mail to Sig2Email for signature stamping'
            }

            # 2. Inbound connector: Sig2Email -> Exchange Online (stamped mail comes back authenticated by certificate)
            if (-not (Get-InboundConnector -Identity '{{Inbound}}' -ErrorAction SilentlyContinue)) {
                New-InboundConnector -Name '{{Inbound}}' `
                    -ConnectorType OnPremises `
                    -SenderDomains '*' `
                    -RequireTls $true `
                    -RestrictDomainsToCertificate $true `
                    -TlsSenderCertificateName $smartHost `
                    -CloudServicesMailEnabled $true `
                    -Comment 'Accepts stamped mail back from Sig2Email'
            }

            # 3. Transport rule: send mail through the connector unless it was already processed (loop prevention).
            #    RuleErrorAction Ignore = fail open: if the rule cannot be evaluated the message is delivered unstamped.
            if (-not (Get-TransportRule -Identity '{{RuleName}}' -ErrorAction SilentlyContinue)) {
                New-TransportRule -Name '{{RuleName}}' `
                    -FromScope InOrganization{{scope}} `
                    -RouteMessageOutboundConnector '{{Outbound}}' `
                    -ExceptIfHeaderContainsMessageHeader 'X-Sig2Email-Processed' `
                    -ExceptIfHeaderContainsWords 'true' `
                    -ExceptIfMessageTypeMatches 'Calendaring' `
                    -RuleErrorAction Ignore `
                    -Priority 0 `
                    -Comments 'Managed by Sig2Email. Disable this rule to bypass signature stamping immediately.'
            }

            Write-Host 'Sig2Email mail flow is configured. Send a test message to verify.' -ForegroundColor Green
            """.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    public static string Remove() => $$"""
        # Sig2Email – remove Exchange Online mail-flow configuration
        $ErrorActionPreference = 'Stop'
        Connect-ExchangeOnline
        Remove-TransportRule -Identity '{{RuleName}}' -Confirm:$false -ErrorAction SilentlyContinue
        Remove-OutboundConnector -Identity '{{Outbound}}' -Confirm:$false -ErrorAction SilentlyContinue
        Remove-InboundConnector -Identity '{{Inbound}}' -Confirm:$false -ErrorAction SilentlyContinue
        Write-Host 'Sig2Email mail flow removed.' -ForegroundColor Green
        """.Replace("\r\n", "\n").Replace("\n", "\r\n");
}
