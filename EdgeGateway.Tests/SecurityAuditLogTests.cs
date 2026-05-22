using EdgeGateway.Core.Security;
using EdgeGateway.Service;
using EdgeGateway.Service.Security;

namespace EdgeGateway.Tests;

public sealed class SecurityAuditLogTests
{
    [Fact]
    public async Task WriteAsync_prunes_security_audit_files_outside_retention_window()
    {
        using TestWorkspace workspace = new();
        string configPath = workspace.WriteDefaultCa270Config(TcpTestPorts.GetFreePort());
        Directory.CreateDirectory(workspace.LogDirectory);
        string oldAuditPath = Path.Combine(workspace.LogDirectory, "security-audit-20000101.jsonl");
        string recentAuditPath = Path.Combine(
            workspace.LogDirectory,
            $"security-audit-{DateTimeOffset.UtcNow.AddDays(-1):yyyyMMdd}.jsonl"
        );
        await File.WriteAllTextAsync(oldAuditPath, "{}");
        await File.WriteAllTextAsync(recentAuditPath, "{}");
        FileSecurityAuditLog auditLog = new(new EdgeGatewayServiceOptions(
            configPath,
            workspace.LogDirectory,
            workspace.SpoolDirectory,
            SecurityAuditRetentionDays: 7
        ));

        await auditLog.WriteAsync(new EdgeGatewayAuditRecord(
            DateTimeOffset.UtcNow,
            "tester",
            EdgeGatewayRole.ConnectionEngineer,
            EdgeGatewayPermission.ControlDevices,
            "output.replay_failed.result",
            "raw-1",
            Allowed: true,
            "Requeued 1 output item(s)."
        ));

        Assert.False(File.Exists(oldAuditPath));
        Assert.True(File.Exists(recentAuditPath));
        Assert.Single(Directory.GetFiles(workspace.LogDirectory, $"security-audit-{DateTimeOffset.UtcNow:yyyyMMdd}.jsonl"));
    }
}
