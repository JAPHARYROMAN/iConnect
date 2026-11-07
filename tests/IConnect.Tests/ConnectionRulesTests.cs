using IConnect.Core;
using Xunit;

namespace IConnect.Tests;

public sealed class ConnectionRulesTests
{
    [Fact]
    public void Stun_configuration_matches_the_shared_direct_only_uri_vectors()
    {
        Assert.Equal("",ConnectionRules.Stun(""));
        foreach(var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory,"stun-uris.tsv")))
        {
            var parts=line.Split('\t',2); var value=parts[1];
            if(parts[0]=="valid") Assert.Equal(value,ConnectionRules.Stun(value));
            else Assert.Throws<InvalidDataException>(()=>ConnectionRules.Stun(value));
        }
        foreach(var value in new[] {"stun:host\n","stun:host\r","stun:ho\0st"," stun:host","stun:host ","stun:"+new string('a',64)+".example", "stun:"+new string('a',257)})
            Assert.Throws<InvalidDataException>(()=>ConnectionRules.Stun(value));
    }
}
