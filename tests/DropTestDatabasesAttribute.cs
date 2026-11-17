// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.TestSupport;

using NUnit.Framework;
using NUnit.Framework.Interfaces;

/// <summary>
/// Drops the databases a test registered through <see cref="TestDatabases"/> once the test has ended, failed tests
/// included. Applied to a test assembly, it runs after every test of the assembly.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class DropTestDatabasesAttribute : Attribute, ITestAction
{
    public ActionTargets Targets => ActionTargets.Test;

    public void AfterTest(ITest test) => TestDatabases.DropOwnedBy(test.Id);

    public void BeforeTest(ITest test) { }
}
