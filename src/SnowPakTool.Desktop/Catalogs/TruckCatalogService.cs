namespace SnowPakTool.Desktop;

internal sealed record TruckCatalogEntry(string Code, string Name, string Class, bool IsOwned, bool IsBaseGame);

internal static class TruckCatalogService
{
    public static IReadOnlyList<OwnedTruck> GetOwned(IEnumerable<TruckCatalogEntry> entries) =>
        entries.Where(entry => entry.IsOwned)
            .Select(entry => new OwnedTruck(entry.Code, entry.Name))
            .DistinctBy(truck => truck.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<CustomTruck> GetAvailableForGrouping(IEnumerable<TruckCatalogEntry> entries) =>
        entries.Where(entry => entry.IsBaseGame || entry.IsOwned)
            .Select(entry => new CustomTruck(entry.Code, entry.Name, TruckClasses.ForTruck(entry.Code, entry.Class)))
            .DistinctBy(truck => truck.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

internal sealed record OwnedTruck(string Code, string Name);
