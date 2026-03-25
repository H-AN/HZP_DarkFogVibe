namespace HZP_DarkFog;

public sealed class HZP_DarkFog_Config
{
    public bool Enable { get; set; } = true;

    public float HumanExposure { get; set; } = 0.45f;

    public float ZombieExposure { get; set; } = 1.25f;

    public string AdminCommandName { get; set; } = "fog";

    public string AdminCommandPermission { get; set; } = "hzp.darkfog.admin";

    public bool HiddenExposureCommandEnabled { get; set; } = true;

    public string HiddenExposureCommandName { get; set; } = "hauhdahsdasd";

    public List<HZP_DarkFog_ZombieGroupConfig> ZombieGroups { get; set; } = [];
}

public sealed class HZP_DarkFog_ZombieGroupConfig
{
    public bool Enable { get; set; } = true;

    public string ZombieClassName { get; set; } = string.Empty;

    public float Exposure { get; set; } = 1.25f;
}
