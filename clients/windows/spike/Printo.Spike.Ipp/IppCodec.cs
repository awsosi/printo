using System.Buffers.Binary;
using System.Text;

namespace Printo.Spike.Ipp;

/// <summary>
/// Minimal IPP binary reader/writer (RFC 8010). Collections are supported because the
/// Windows IPP Class Driver reads <c>media-col-database</c> when it builds the queue
/// capabilities, and a printer that omits them is treated as unconfigurable.
/// </summary>
internal static class IppCodec
{
    public static IppMessage Decode(byte[] buffer)
    {
        var message = new IppMessage();
        if (buffer.Length < 8)
        {
            throw new InvalidDataException($"IPP message too short ({buffer.Length} bytes)");
        }

        message.VersionMajor = buffer[0];
        message.VersionMinor = buffer[1];
        message.Code = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2));
        message.RequestId = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(4));

        var offset = 8;
        IppGroup? group = null;
        IppAttribute? previous = null;

        // Collections nest, so member attributes are appended to whichever collection value
        // is currently open. An empty stack means "append to the current group".
        var open = new Stack<IppValue>();

        while (offset < buffer.Length)
        {
            var tag = (IppTag)buffer[offset++];

            if (tag == IppTag.EndOfAttributes)
            {
                message.Data = buffer[offset..];
                return message;
            }

            if ((byte)tag <= 0x0F)
            {
                group = message.AddGroup(tag);
                previous = null;
                open.Clear();
                continue;
            }

            var nameLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset));
            offset += 2;
            var name = Encoding.UTF8.GetString(buffer, offset, nameLength);
            offset += nameLength;

            var valueLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset));
            offset += 2;
            var raw = buffer[offset..(offset + valueLength)];
            offset += valueLength;

            if (tag == IppTag.EndCollection)
            {
                if (open.Count > 0)
                {
                    open.Pop();
                }

                previous = null;
                continue;
            }

            if (tag == IppTag.MemberAttrName)
            {
                // The member name arrives as its own pseudo-attribute; the next value belongs to it.
                previous = new IppAttribute(Encoding.UTF8.GetString(raw));
                if (open.Count > 0)
                {
                    open.Peek().Members.Add(previous);
                }
                else
                {
                    group?.Attributes.Add(previous);
                }

                continue;
            }

            var value = new IppValue(tag, raw);

            if (nameLength == 0 && previous is not null)
            {
                // Additional value for the attribute (or member) named most recently.
                previous.Values.Add(value);
            }
            else
            {
                var attribute = new IppAttribute(name);
                attribute.Values.Add(value);
                if (open.Count > 0)
                {
                    open.Peek().Members.Add(attribute);
                }
                else
                {
                    group?.Attributes.Add(attribute);
                }

                previous = attribute;
            }

            if (tag == IppTag.BegCollection)
            {
                open.Push(value);
                previous = null;
            }
        }

        return message;
    }

    public static byte[] Encode(IppMessage message)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(message.VersionMajor);
        stream.WriteByte(message.VersionMinor);
        WriteUInt16(stream, message.Code);
        WriteInt32(stream, message.RequestId);

        foreach (var group in message.Groups)
        {
            stream.WriteByte((byte)group.Tag);
            foreach (var attribute in group.Attributes)
            {
                WriteAttribute(stream, attribute);
            }
        }

        stream.WriteByte((byte)IppTag.EndOfAttributes);
        if (message.Data.Length > 0)
        {
            stream.Write(message.Data);
        }

        return stream.ToArray();
    }

    private static void WriteAttribute(Stream stream, IppAttribute attribute)
    {
        for (var i = 0; i < attribute.Values.Count; i++)
        {
            var value = attribute.Values[i];
            var name = i == 0 ? attribute.Name : string.Empty;

            if (value.Tag == IppTag.BegCollection)
            {
                stream.WriteByte((byte)IppTag.BegCollection);
                WriteString(stream, name);
                WriteUInt16(stream, 0);

                foreach (var member in value.Members)
                {
                    WriteMember(stream, member);
                }

                stream.WriteByte((byte)IppTag.EndCollection);
                WriteUInt16(stream, 0);
                WriteUInt16(stream, 0);
                continue;
            }

            stream.WriteByte((byte)value.Tag);
            WriteString(stream, name);
            WriteUInt16(stream, (ushort)value.Raw.Length);
            stream.Write(value.Raw);
        }
    }

    private static void WriteMember(Stream stream, IppAttribute member)
    {
        stream.WriteByte((byte)IppTag.MemberAttrName);
        WriteUInt16(stream, 0);
        WriteString(stream, member.Name);

        for (var i = 0; i < member.Values.Count; i++)
        {
            var value = member.Values[i];
            if (value.Tag == IppTag.BegCollection)
            {
                stream.WriteByte((byte)IppTag.BegCollection);
                WriteUInt16(stream, 0);
                WriteUInt16(stream, 0);
                foreach (var nested in value.Members)
                {
                    WriteMember(stream, nested);
                }

                stream.WriteByte((byte)IppTag.EndCollection);
                WriteUInt16(stream, 0);
                WriteUInt16(stream, 0);
                continue;
            }

            stream.WriteByte((byte)value.Tag);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, (ushort)value.Raw.Length);
            stream.Write(value.Raw);
        }
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt16(stream, (ushort)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }
}

/// <summary>Fluent helpers so the printer attribute table reads like the PWG spec.</summary>
internal static class IppGroupExtensions
{
    public static IppGroup Text(this IppGroup group, string name, IppTag tag, params string[] values)
    {
        var attribute = new IppAttribute(name);
        foreach (var value in values)
        {
            attribute.Values.Add(new IppValue(tag, Encoding.UTF8.GetBytes(value)));
        }

        group.Attributes.Add(attribute);
        return group;
    }

    public static IppGroup Keyword(this IppGroup group, string name, params string[] values) =>
        group.Text(name, IppTag.Keyword, values);

    public static IppGroup Number(this IppGroup group, string name, IppTag tag, params int[] values)
    {
        var attribute = new IppAttribute(name);
        foreach (var value in values)
        {
            var raw = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(raw, value);
            attribute.Values.Add(new IppValue(tag, raw));
        }

        group.Attributes.Add(attribute);
        return group;
    }

    public static IppGroup Integer(this IppGroup group, string name, params int[] values) =>
        group.Number(name, IppTag.Integer, values);

    public static IppGroup Enum(this IppGroup group, string name, params int[] values) =>
        group.Number(name, IppTag.Enum, values);

    public static IppGroup Bool(this IppGroup group, string name, bool value)
    {
        var attribute = new IppAttribute(name);
        attribute.Values.Add(new IppValue(IppTag.Boolean, [(byte)(value ? 1 : 0)]));
        group.Attributes.Add(attribute);
        return group;
    }

    public static IppGroup Range(this IppGroup group, string name, int lower, int upper)
    {
        var raw = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(raw, lower);
        BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan(4), upper);
        var attribute = new IppAttribute(name);
        attribute.Values.Add(new IppValue(IppTag.RangeOfInteger, raw));
        group.Attributes.Add(attribute);
        return group;
    }

    public static IppGroup Resolution(this IppGroup group, string name, params int[] dpi)
    {
        var attribute = new IppAttribute(name);
        foreach (var value in dpi)
        {
            var raw = new byte[9];
            BinaryPrimitives.WriteInt32BigEndian(raw, value);
            BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan(4), value);
            raw[8] = 3; // dots per inch
            attribute.Values.Add(new IppValue(IppTag.Resolution, raw));
        }

        group.Attributes.Add(attribute);
        return group;
    }

    /// <summary>Adds a 1setOf collection attribute, e.g. <c>media-col-database</c>.</summary>
    public static IppGroup Collections(this IppGroup group, string name, IEnumerable<IppAttribute[]> entries)
    {
        var attribute = new IppAttribute(name);
        foreach (var members in entries)
        {
            var value = new IppValue(IppTag.BegCollection, []);
            value.Members.AddRange(members);
            attribute.Values.Add(value);
        }

        group.Attributes.Add(attribute);
        return group;
    }

    public static IppAttribute Member(string name, IppTag tag, params int[] values)
    {
        var attribute = new IppAttribute(name);
        foreach (var value in values)
        {
            var raw = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(raw, value);
            attribute.Values.Add(new IppValue(tag, raw));
        }

        return attribute;
    }

    public static IppAttribute MemberText(string name, IppTag tag, string value)
    {
        var attribute = new IppAttribute(name);
        attribute.Values.Add(new IppValue(tag, Encoding.UTF8.GetBytes(value)));
        return attribute;
    }

    public static IppAttribute MemberCollection(string name, params IppAttribute[] members)
    {
        var attribute = new IppAttribute(name);
        var value = new IppValue(IppTag.BegCollection, []);
        value.Members.AddRange(members);
        attribute.Values.Add(value);
        return attribute;
    }
}
