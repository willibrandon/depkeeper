#!/usr/bin/env dotnet
#:include WorkflowExitCode.cs

using Depkeeper.Cli;

if (WorkflowExitCode.Map(0) != 0 || WorkflowExitCode.Map(2) != 0 || WorkflowExitCode.Map(1) != 1 ||
    WorkflowExitCode.Map(130) != 130) return 1;
return 0;
