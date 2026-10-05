using System;
using System.Collections.Generic;
using Effect_System;
using Effect_System.Effects;
using Effect_System.Trigger_System;
using Effect_System.Trigger_System.Trigger_Mechanisms;
using Effect_System.Trigger_System.Trigger_Reactions;

namespace TinyRogues.Chinese;

// Operate only inside a complex item description call, before native token expansion.
// Do not patch the small effect getters: IL2CPP can share their native addresses.
internal sealed class DescriptionScope
{
    private readonly List<Action> _restore = new();
    private readonly HashSet<IntPtr> _seen = new();
    internal void Text(Func<string> read, Action<string> write, string context = "description-template")
    {
        var source = read();
        if (string.IsNullOrEmpty(source)) return;
        var translated = Plugin.Current.Translate(source, context, false);
        if (translated == source) return;
        _restore.Add(() => write(source));
        write(translated);
    }
    internal void Effects(Il2CppSystem.Collections.Generic.List<Effect>? effects)
    {
        if (effects == null) return;
        for (var index = 0; index < effects.Count; index++) Visit(effects[index]);
    }
    private void Reactions(Il2CppSystem.Collections.Generic.List<TriggerReaction>? reactions)
    {
        if (reactions == null) return;
        for (var index = 0; index < reactions.Count; index++)
        {
            var attack = reactions[index]?.TryCast<AttackActionTriggerReaction>();
            if (attack != null) Text(() => attack.actionName, value => attack.actionName = value, "weapon-action-name");
        }
    }
    internal void Visit(Il2CppSystem.Object? node)
    {
        if (node == null || !_seen.Add(node.Pointer)) return;
        var trigger = node.TryCast<Trigger>();
        if (trigger != null)
        {
            Text(() => trigger.description, value => trigger.description = value);
            Reactions(trigger.reactions); return;
        }
        var periodic = node.TryCast<PeriodicallyTrigger>();
        if (periodic != null)
        {
            Text(() => periodic.description, value => periodic.description = value);
            Reactions(periodic.reactions); return;
        }
        var standing = node.TryCast<StandingStillTrigger>();
        if (standing != null)
        {
            Text(() => standing.description, value => standing.description = value);
            Reactions(standing.reactions); return;
        }
        var provider = node.TryCast<DescriptionProviderEffect>();
        if (provider != null) { Visit(provider.objectWithDescription); return; }
        var dummy = node.TryCast<DummyDescriptionEffect>();
        if (dummy != null)
        {
            Text(() => dummy.activeDescription, value => dummy.activeDescription = value);
            Text(() => dummy.informativeDescription, value => dummy.informativeDescription = value); return;
        }
        var bonus = node.TryCast<RegisterBonusBuffDebuffEffect>();
        if (bonus != null)
        {
            Text(() => bonus.activeDescription, value => bonus.activeDescription = value);
            Text(() => bonus.informativeDescription, value => bonus.informativeDescription = value); return;
        }
        var emitter = node.TryCast<Combat.Damage.PeriodicalEffectEmitter>();
        if (emitter != null) { Text(() => emitter.description, value => emitter.description = value); return; }
        var onDamage = node.TryCast<Combat.Damage.EffectOnDamageTrigger>();
        if (onDamage != null) Text(() => onDamage.description, value => onDamage.description = value);
    }
    internal void Restore()
    {
        Exception? failure = null;
        for (var index = _restore.Count - 1; index >= 0; index--)
            try { _restore[index](); } catch (Exception error) { failure ??= error; }
        _restore.Clear();
        if (failure != null) throw failure;
    }
}
