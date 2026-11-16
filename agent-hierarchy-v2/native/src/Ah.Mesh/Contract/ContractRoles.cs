using Ah.Config;
using Ah.Core.Json;

namespace Ah.Mesh;

/// <summary>The behavioural contract text of one built-in chain role, as <c>GET /v1/contract/role</c> serves it.</summary>
public interface IContractRoles
{
    /// <summary>
    /// <c>{role, text}</c> for a built-in role of a chain class (advise, design, review, implement), else null. Only a known
    /// role name resolves; nothing of the input becomes a path. Null too when a contract file is missing.
    /// </summary>
    JsonObject? Role(string? role);
}

internal sealed class ContractRoles(IConfigCore config, IRoleContracts contracts) : IContractRoles
{
    public JsonObject? Role(string? role)
    {
        if (role is null || !config.IsBuiltinRole(role))
        {
            return null;
        }

        var cls = config.RoleClass(role, null);
        if (string.IsNullOrEmpty(cls) || config.ClassProp(role, null, "chain") is not { IsTruthy: true })
        {
            return null;
        }

        var text = contracts.ContractText(role, null, cls, null);
        return text is null ? null : new JsonObject { ["role"] = role, ["text"] = text };
    }
}
