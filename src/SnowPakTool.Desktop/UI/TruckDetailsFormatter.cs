using System.Text;
using System.Text.RegularExpressions;

namespace SnowPakTool.Desktop;

internal static class TruckDetailsFormatter
{
    public static string Format(RegionTruckEntry entry)
    {
        var text = new StringBuilder();
        text.AppendLine("INTERNAL EQUIPMENT / DRIVETRAIN");
        if (entry.InternalEquipment is not { Count: > 0 }) text.AppendLine("Internal equipment could not be resolved from this placement.");
        else foreach (var item in entry.InternalEquipment.Where(item => item.Slot != "Vehicle definition"))
        {
            text.AppendLine($"{item.Slot}: {item.Name}");
        }
        var visual = entry.InstalledAddons?.Where(addon => addon.IsVisual
            && !addon.Category.Equals("awd", StringComparison.OrdinalIgnoreCase)
            && !addon.Category.Equals("diff_lock", StringComparison.OrdinalIgnoreCase))
            .Select(addon => "• " + addon.Name + (addon.Category.Length == 0 ? "" : " (" + addon.Category.Replace('_', ' ') + ")")).ToArray();
        Section("VISUAL ADDONS", visual is null ? "Installed addon data could not be read."
            : visual.Length == 0 ? "None." : string.Join(Environment.NewLine, visual));
        var vehicle = entry.InternalEquipment?.FirstOrDefault(item => item.Slot == "Vehicle definition");
        if (vehicle is not null) Section("VEHICLE PROPERTIES", Regex.Replace(
            Regex.Replace(vehicle.DefinitionDetails, @"\b(?:TruckData|GameData)\.", ""), @"\s*=\s*", " = "));
        Section("TIRE / WHEEL DETAILS", entry.TireDetails);
        return text.ToString().TrimEnd();

        void Section(string title, string body)
        { text.AppendLine(); text.AppendLine(title); text.AppendLine(body.Length == 0 ? "Not available." : body); }
    }
}
