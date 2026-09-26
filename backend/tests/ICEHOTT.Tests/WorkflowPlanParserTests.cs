using ICEHOTT.Application.Workflows;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Tests;

public sealed class WorkflowPlanParserTests
{
    [Fact]
    public void Parses_Tool_Delay_And_Checkpoint_Metadata()
    {
        var plan = WorkflowPlanParser.Parse(
            """
            {
              "steps": [
                {
                  "key": "echo",
                  "type": "tool",
                  "toolName": "workspace.echo",
                  "arguments": { "text": "hello" },
                  "retry": {
                    "maxAttempts": 3,
                    "initialDelaySeconds": 2,
                    "maxDelaySeconds": 30,
                    "backoffMultiplier": 2
                  }
                },
                {
                  "key": "wait",
                  "type": "delay",
                  "dependsOn": ["echo"],
                  "delaySeconds": 10
                },
                {
                  "key": "approve",
                  "type": "checkpoint",
                  "dependsOn": ["wait"],
                  "minimumApproverRole": "Owner",
                  "requiresDifferentApprover": true
                }
              ]
            }
            """);

        Assert.Equal(3, plan.Steps.Count);

        var tool = plan.Steps[0];
        Assert.Equal(WorkflowStepType.Tool, tool.Type);
        Assert.Equal("workspace.echo", tool.ToolName);
        Assert.Contains("\"text\":\"hello\"", tool.ArgumentsJson!.Replace(" ", ""));
        Assert.Equal(3, tool.Retry.MaxAttempts);

        var delay = plan.Steps[1];
        Assert.Equal(10, delay.DelaySeconds);

        var checkpoint = plan.Steps[2];
        Assert.Equal(WorkspaceRole.Owner, checkpoint.MinimumApproverRole);
        Assert.True(checkpoint.RequiresDifferentApprover);
    }

    [Fact]
    public void Rejects_Duplicate_Json_Properties()
    {
        Assert.Throws<WorkflowPlanException>(() =>
            WorkflowPlanParser.Parse(
                """
                {
                  "steps": [
                    {
                      "key": "one",
                      "key": "two",
                      "type": "delay",
                      "delaySeconds": 1
                    }
                  ]
                }
                """));
    }

    [Fact]
    public void Rejects_Cycles()
    {
        Assert.Throws<WorkflowPlanException>(() =>
            WorkflowPlanParser.Parse(
                """
                {
                  "steps": [
                    { "key": "a", "type": "delay", "delaySeconds": 1, "dependsOn": ["b"] },
                    { "key": "b", "type": "delay", "delaySeconds": 1, "dependsOn": ["a"] }
                  ]
                }
                """));
    }

    [Fact]
    public void Rejects_Multiple_Entry_Steps()
    {
        Assert.Throws<WorkflowPlanException>(() =>
            WorkflowPlanParser.Parse(
                """
                {
                  "steps": [
                    { "key": "a", "type": "delay", "delaySeconds": 1 },
                    { "key": "b", "type": "delay", "delaySeconds": 1 }
                  ]
                }
                """));
    }

    [Fact]
    public void Rejects_Unknown_Dependency()
    {
        Assert.Throws<WorkflowPlanException>(() =>
            WorkflowPlanParser.Parse(
                """
                {
                  "steps": [
                    {
                      "key": "a",
                      "type": "delay",
                      "delaySeconds": 1,
                      "dependsOn": ["missing"]
                    }
                  ]
                }
                """));
    }

    [Fact]
    public void Retry_Backoff_Is_Bounded()
    {
        var policy = new WorkflowRetryPolicy(
            5,
            10,
            25,
            2);

        Assert.Equal(TimeSpan.FromSeconds(10), policy.DelayForAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(20), policy.DelayForAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(25), policy.DelayForAttempt(3));
        Assert.Equal(TimeSpan.FromSeconds(25), policy.DelayForAttempt(10));
    }
}
