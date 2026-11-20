namespace TacoFactory.Core.Sim;

public enum ClassId : byte { Knight = 0, Paladin = 1, Wizard = 2, Healer = 3 }

/// <summary>
/// Base numbers per class. What a character can do comes from the skills they learn;
/// the class sets health, mana, speed, starting skills and how many build points each skill costs (skills.json).
/// </summary>
public sealed record ClassDef(ClassId Id, string Name, float HealthMul, float ManaMul, float DamageMul, float Speed, float DamageReduction);

public static class Classes
{
    public static readonly ClassDef Knight = new(ClassId.Knight, "Knight", 1.30f, 0.60f, 1.10f, 240, 0.15f);
    public static readonly ClassDef Paladin = new(ClassId.Paladin, "Paladin", 1.15f, 0.90f, 1.00f, 240, 0.10f);
    public static readonly ClassDef Wizard = new(ClassId.Wizard, "Wizard", 0.80f, 1.50f, 1.15f, 230, 0.00f);
    public static readonly ClassDef Healer = new(ClassId.Healer, "Healer", 0.90f, 1.40f, 0.90f, 235, 0.05f);

    public static ClassDef Get(ClassId id) => id switch
    {
        ClassId.Knight => Knight,
        ClassId.Paladin => Paladin,
        ClassId.Wizard => Wizard,
        ClassId.Healer => Healer,
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static readonly ClassId[] All = [ClassId.Knight, ClassId.Paladin, ClassId.Wizard, ClassId.Healer];
}
