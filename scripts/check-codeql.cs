#!/usr/bin/env dotnet

using System.Text.Json;

if (args.Length != 1)
{
    Console.Error.WriteLine("Provide the CodeQL SARIF result file.");
    return 2;
}

using var document = JsonDocument.Parse(File.ReadAllText(args[0]));
var findings = document.RootElement.GetProperty("runs").EnumerateArray()
    .Where(run => run.TryGetProperty("results", out _))
    .SelectMany(run => run.GetProperty("results").EnumerateArray()).ToArray();
foreach (var finding in findings) Console.Error.WriteLine(finding.GetProperty("ruleId").GetString());
Console.WriteLine($"CodeQL findings: {findings.Length}.");
return findings.Length == 0 ? 0 : 1;
