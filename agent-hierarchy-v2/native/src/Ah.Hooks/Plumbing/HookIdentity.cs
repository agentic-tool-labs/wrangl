namespace Ah.Hooks;

/// <summary>What a hook unit does with the payload session's identity, which decides how it is treated when that session does not belong to the hook's process.</summary>
internal enum HookIdentityKind
{
    /// <summary>The unit writes or decides by the payload session's records: on a mismatch a gate denies and any other unit skips its effects.</summary>
    Grants,

    /// <summary>The unit can only deny, so it runs whatever the payload says.</summary>
    RestrictOnly,

    /// <summary>The unit neither grants nor restricts by session identity.</summary>
    None,
}

/// <summary>Declares a hook unit's <see cref="HookIdentityKind"/>. Every registered unit's class carries one; there is no default.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
internal sealed class HookIdentityAttribute(HookIdentityKind kind) : Attribute
{
    public HookIdentityKind Kind { get; } = kind;
}
