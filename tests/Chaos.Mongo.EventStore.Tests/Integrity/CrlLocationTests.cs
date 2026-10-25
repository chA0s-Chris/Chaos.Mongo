// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.EventStore.Tests.Integrity;

using Chaos.Mongo.EventStore.Integrity;
using FluentAssertions;
using NUnit.Framework;

public class CrlLocationTests
{
    [Test]
    [TestCase("http://tsa.test/ca.crl", "http://tsa.test/ca.crl")]
    [TestCase("HTTP://TSA.Test/CA.crl", "http://tsa.test/CA.crl")]
    [TestCase("http://tsa.test:80/ca.crl", "http://tsa.test/ca.crl")]
    [TestCase("https://tsa.test:443/ca.crl", "https://tsa.test/ca.crl")]
    [TestCase("https://tsa.test:8443/ca.crl", "https://tsa.test:8443/ca.crl")]
    [TestCase("http://tsa.test", "http://tsa.test/")]
    [TestCase("http://tsa.test?x=1", "http://tsa.test/?x=1")]
    [TestCase("http://tsa.test/a/./b/../ca.crl", "http://tsa.test/a/ca.crl")]
    [TestCase("http://tsa.test/a/..", "http://tsa.test/")]
    [TestCase("http://user:secret@tsa.test/ca.crl#part", "http://tsa.test/ca.crl")]
    [TestCase("http://[::1]:8080/ca.crl", "http://[::1]:8080/ca.crl")]
    [TestCase("http://tsa.test/ca%7e.crl?", "http://tsa.test/ca%7e.crl?")]
    public void Normalize_HttpLocation_ReturnsNormalizedForm(String location, String expected)
        => CrlLocation.Normalize(location).Should().Be(expected);

    [Test]
    [TestCase("ldap://tsa.test/ca.crl")]
    [TestCase("tsa.test/ca.crl")]
    [TestCase("http:///ca.crl")]
    [TestCase("http://tsa.test:port/ca.crl")]
    [TestCase("http://[::1/ca.crl")]
    [TestCase("http://tsa.test:70000/ca.crl")]
    [TestCase("http://ts a.test/ca.crl")]
    [TestCase("http://tsa_test!/ca.crl")]
    [TestCase("http://[zz]/ca.crl")]
    [TestCase("http://[]/ca.crl")]
    public void Normalize_NotAnHttpLocation_ReturnsNull(String location)
        => CrlLocation.Normalize(location).Should().BeNull();
}
