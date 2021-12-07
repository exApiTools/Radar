namespace Radar;

public class TargetDescription
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; }
    public int ExpectedCount { get; set; } = 1;
    public TargetType TargetType { get; set; }
}
