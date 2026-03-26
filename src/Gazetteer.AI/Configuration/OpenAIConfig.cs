namespace Gazetteer.AI.Configuration;

public class OpenAIConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public string DeploymentName { get; set; } = "gpt-4o";
    public string Endpoint { get; set; } = string.Empty;
}
