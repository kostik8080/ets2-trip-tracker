namespace EtsTracker.App;

/// <summary>Пункт выпадающего списка: значение и подпись по-русски.</summary>
public sealed record Choice<T>(T Value, string Name)
{
    public override string ToString() => Name;
}

public static class Choices
{
    public static List<Choice<DriverRank>> Ranks { get; } =
        Enum.GetValues<DriverRank>().Select(r => new Choice<DriverRank>(r, Names.Of(r))).ToList();

    public static List<Choice<CargoClass>> CargoClasses { get; } =
        Enum.GetValues<CargoClass>().Select(c => new Choice<CargoClass>(c, Names.Of(c))).ToList();

    public static List<Choice<TripKind>> TripKinds { get; } =
        Enum.GetValues<TripKind>().Select(k => new Choice<TripKind>(k, Names.Of(k))).ToList();
}
