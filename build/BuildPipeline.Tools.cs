// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace BuildPipeline;

using Fallout.Common;
using Fallout.Common.Tools.DotNet;

internal partial class BuildPipeline
{
    private Target RestoreTools => target =>
        target.Executes(() => DotNetTasks.DotNetToolRestore());
}
