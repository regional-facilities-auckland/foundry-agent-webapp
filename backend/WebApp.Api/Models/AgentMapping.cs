namespace WebApp.Api.Models;

public record AgentMapping
{
    public required string Value { get; init; }
    public string? Label { get; init; }
    public required string AgentId { get; init; }
}
