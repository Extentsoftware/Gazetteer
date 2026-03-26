namespace Gazetteer.AI.Configuration;

public class OpenAIConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = "gpt-4o";
    public string? OrgId { get; set; }
}
