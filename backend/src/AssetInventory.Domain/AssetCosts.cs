namespace AssetInventory.Domain;

public static class AssetCosts
{
    public const decimal Maximum = 99_999_999_999_999_999.99m;
    // At 14 integer digits Excel's 15 significant digits cannot preserve every cent.
    public const decimal XlsxTextMinimum = 10_000_000_000_000m;

    public static bool IsSupported(decimal value) =>
        value is >= 0 and <= Maximum && decimal.Round(value, 2) == value;
}
