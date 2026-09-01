namespace RxV4A.Host;

public static class SettingsValidator
{
    public static void Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        foreach (var activityId in settings.Activities.Keys)
        {
            ValidateLogicalId(activityId, "Activity");
        }

        foreach (var blockerId in settings.PowerOnBlockers)
        {
            ValidateLogicalId(blockerId, "Power-on blocker");
        }

        var duplicateBlocker = settings.PowerOnBlockers
            .GroupBy(item => item, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateBlocker is not null)
        {
            throw new InvalidDataException($"Duplicate power-on blocker '{duplicateBlocker.Key}'.");
        }

        var validControls = settings.Activities.Keys.Concat(settings.PowerOnBlockers)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (controlId, binding) in settings.ActionBindings)
        {
            if (!validControls.Contains(controlId))
            {
                throw new InvalidDataException($"Unknown action binding target '{controlId}'.");
            }

            foreach (var actionId in binding.OnActivate.Concat(binding.OnDeactivate))
            {
                ValidateLogicalId(actionId, "Registered action");
            }
        }

        ValidateGlobalHotkeys(settings);
    }

    public static void ValidateGlobalHotkeys(AppSettings settings)
    {
        var assigned = settings.GlobalHotkeys.Where(item => !string.IsNullOrWhiteSpace(item.ActionId)).ToArray();
        foreach (var binding in assigned)
        {
            if (binding.Gesture.VirtualKey is < 0x7C or > 0x87)
            {
                throw new InvalidDataException("Global hotkeys are limited to F13 through F24.");
            }

            var actionId = binding.ActionId!;
            if (!IsKnownHotkeyAction(actionId, settings))
            {
                throw new InvalidDataException($"Unknown hotkey action '{actionId}'.");
            }
        }

        var duplicate = assigned.GroupBy(item => item.Gesture.Identity, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Duplicate global hotkey '{duplicate.Key}'.");
        }
    }

    public static void ValidateLogicalId(string value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new InvalidDataException($"{kind} ID must use 1-64 ASCII letters, digits, '.', '_' or '-'.");
        }
    }

    private static bool IsKnownHotkeyAction(string actionId, AppSettings settings)
    {
        if (string.Equals(actionId, HotkeyActionIds.PowerOn, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actionId, HotkeyActionIds.PowerStandby, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HasRegisteredSuffix(actionId, "activity:", settings.Activities.Keys) ||
               HasRegisteredSuffix(actionId, "blocker:", settings.PowerOnBlockers) ||
               HasValidActionSuffix(actionId);
    }

    private static bool HasRegisteredSuffix(string value, string prefix, IEnumerable<string> registered) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        registered.Contains(value[prefix.Length..], StringComparer.OrdinalIgnoreCase);

    private static bool HasValidActionSuffix(string value)
    {
        const string prefix = "action:";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            ValidateLogicalId(value[prefix.Length..], "Registered action");
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
