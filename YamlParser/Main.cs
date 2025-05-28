namespace YamlParser;

using System.Net;
using ARSoft.Tools.Net;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

public class SettingDns
{
    public IPAddress Address { get; set; } = new IPAddress(0);
    public int Port { get; set; } = 53;
    public List<DomainName> Domains { get; set; } = [];

    public override bool Equals(object? obj)
    {
        if (obj is SettingDns dns)
        {
            return Address.Equals(dns.Address)
                && Port == dns.Port
                && Domains.Except(dns.Domains).ToList().Count == 0
                && dns.Domains.Except(Domains).ToList().Count == 0;
        }
        return false;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Address, Domains);
    }
}

public class Setting
{
    public string Mode { get; set; } = "";
    public int PollingInterval { get; set; } = 5;
    public int Port { get; set; } = 53;
    public Dictionary<string, SettingDns> Dns { get; set; } = [];
}

class DomainNameConverter : IYamlTypeConverter
{
    public bool Accepts(Type type)
    {
        return type == typeof(DomainName);
    }

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var value = parser.Consume<Scalar>().Value;
        return DomainName.Parse(value);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is DomainName domainName)
        {
            emitter.Emit(
                new Scalar(
                    AnchorName.Empty,
                    TagName.Empty,
                    domainName.ToString(),
                    ScalarStyle.DoubleQuoted,
                    true,
                    false
                )
            );
        }
    }
}

class IPAddressConverter : IYamlTypeConverter
{
    public bool Accepts(Type type)
    {
        return type == typeof(IPAddress);
    }

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var value = parser.Consume<Scalar>().Value;
        return IPAddress.Parse(value);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is IPAddress address)
        {
            emitter.Emit(
                new Scalar(
                    AnchorName.Empty,
                    TagName.Empty,
                    address.ToString(),
                    ScalarStyle.DoubleQuoted,
                    true,
                    false
                )
            );
        }
    }
}

[YamlStaticContext]
[YamlSerializable(typeof(SettingDns))]
[YamlSerializable(typeof(Setting))]
public partial class StaticContext : YamlDotNet.Serialization.StaticContext { }

public class Parser
{
    public static IDeserializer Create()
    {
        return new StaticDeserializerBuilder(new StaticContext())
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new DomainNameConverter())
            .WithTypeConverter(new IPAddressConverter())
            .Build();
    }
}
