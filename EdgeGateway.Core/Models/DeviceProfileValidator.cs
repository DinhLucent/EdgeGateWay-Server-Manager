namespace EdgeGateway.Core.Models;

public static class DeviceProfileValidator
{
    public static void Validate(DeviceProfile profile)
    {
        RequireText(profile.DeviceId, nameof(profile.DeviceId));
        RequireText(profile.Name, nameof(profile.Name));
        ValidateCommonOptions(profile);
        ValidateTransport(profile);
        ValidateRawBoundary(profile);
        ValidateParser(profile);
        ValidateOutput(profile);
    }

    private static void ValidateCommonOptions(DeviceProfile profile)
    {
        RequireText(profile.Encoding, nameof(profile.Encoding));
        if (profile.ReadTimeoutMs < 1)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires readTimeoutMs of at least 1."
            );
        }

        ValidateAstmOptions(profile);
    }

    private static void ValidateTransport(DeviceProfile profile)
    {
        switch (profile.Transport)
        {
            case DeviceTransport.TcpClient:
                RequireText(profile.Host, nameof(profile.Host));
                RequirePort(profile);
                break;
            case DeviceTransport.TcpServer:
                RequirePort(profile);
                break;
            case DeviceTransport.Rs232Serial:
                RequireText(profile.ComPort, nameof(profile.ComPort));
                ValidateSerialOptions(profile);
                break;
            case DeviceTransport.FolderWatcher:
                RequireText(profile.Folder, nameof(profile.Folder));
                break;
            default:
                throw new InvalidOperationException(
                    $"Device '{profile.DeviceId}' has unsupported transport '{profile.Transport}'."
                );
        }
    }

    private static void ValidateSerialOptions(DeviceProfile profile)
    {
        if (profile.BaudRate < 300 || profile.BaudRate > 921_600)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires baudRate between 300 and 921600."
            );
        }

        if (profile.DataBits is < 5 or > 8)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires dataBits between 5 and 8."
            );
        }

        if (profile.WriteTimeoutMs < 1)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires writeTimeoutMs of at least 1."
            );
        }

        if (profile.ReadCoalesceMs is < 0 or > 5_000)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires readCoalesceMs between 0 and 5000."
            );
        }

        if (profile.MaxBufferBytes < 1_024)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires maxBufferBytes of at least 1024."
            );
        }
    }

    private static void ValidateAstmOptions(DeviceProfile profile)
    {
        if (profile.Protocol != DeviceProtocol.Astm)
        {
            return;
        }

        if (profile.Astm.AckTimeoutMs < 1)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires astm.ackTimeoutMs of at least 1."
            );
        }

        if (profile.Astm.InterFrameTimeoutMs < 1)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires astm.interFrameTimeoutMs of at least 1."
            );
        }

        if (profile.Astm.MaxFrameRetries < 0)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires astm.maxFrameRetries of at least 0."
            );
        }

        if (profile.Astm.MaxTransactionBytes < 1)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires astm.maxTransactionBytes of at least 1."
            );
        }

        if (profile.Astm.MaxFrameCount < 1)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires astm.maxFrameCount of at least 1."
            );
        }

        if (profile.Astm.MaxOutboundFramePayloadBytes < 1)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires astm.maxOutboundFramePayloadBytes of at least 1."
            );
        }

        if (profile.AstmOrderReply.Enabled && profile.Transport != DeviceTransport.Rs232Serial)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' can enable ASTM order reply only for RS232 serial in this slice."
            );
        }

        if (profile.AstmOrderReply.Enabled
            && string.IsNullOrWhiteSpace(profile.AstmOrderReply.Host))
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires astm.orderReply.host when order reply is enabled."
            );
        }

        if (profile.AstmOrderReply.Enabled
            && profile.AstmOrderReply.Template.Records.Count == 0)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires at least one astm.orderReply.template record."
            );
        }
    }

    private static void ValidateRawBoundary(DeviceProfile profile)
    {
        if (profile.Protocol is not DeviceProtocol.Raw and not DeviceProtocol.RawText)
        {
            return;
        }

        if (profile.RawBoundary.MaxMessageBytes < 1)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires rawBoundary.maxMessageBytes of at least 1."
            );
        }

        if (profile.RawBoundary.Mode == RawBoundaryMode.FixedLength
            && profile.RawBoundary.FixedLength is null or < 1)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires rawBoundary.fixedLength of at least 1."
            );
        }

        if (profile.RawBoundary.Mode == RawBoundaryMode.DelimitedText
            && string.Equals(profile.RawBoundary.Delimiter, "custom", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(profile.RawBoundary.CustomDelimiter))
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires rawBoundary.customDelimiter when rawBoundary.delimiter is custom."
            );
        }

        if (profile.RawBoundary.Mode == RawBoundaryMode.CustomBytes
            && (string.IsNullOrWhiteSpace(profile.RawBoundary.StartBytes)
                || string.IsNullOrWhiteSpace(profile.RawBoundary.EndBytes)))
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires rawBoundary.startBytes and rawBoundary.endBytes for custom bytes mode."
            );
        }
    }

    private static void ValidateParser(DeviceProfile profile)
    {
        if (!profile.Parser.Enabled || profile.Parser.Type == ParserProfileType.None)
        {
            return;
        }

        if (profile.Parser.OutputMode != ParserOutputMode.MetadataOnly)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' supports parser.outputMode metadataOnly only in this slice."
            );
        }

        switch (profile.Parser.Type)
        {
            case ParserProfileType.RegexLines:
                if (profile.Parser.RegexLineRules.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Device '{profile.DeviceId}' requires parser.regexLineRules for regexLines parser."
                    );
                }

                foreach (RegexLineRuleOptions rule in profile.Parser.RegexLineRules)
                {
                    RequireText(rule.Name, "parser.regexLineRules.name");
                    RequireText(rule.Pattern, "parser.regexLineRules.pattern");
                    RequireText(rule.ValueGroup, "parser.regexLineRules.valueGroup");
                }
                break;
            case ParserProfileType.DelimiterTable:
                RequireText(profile.Parser.DelimiterTable.Delimiter, "parser.delimiterTable.delimiter");
                RequireParserColumn(profile, "testCode");
                RequireParserColumn(profile, "value");
                break;
            case ParserProfileType.FixedColumnReport:
                RequireFixedColumn(profile, "testCode");
                RequireFixedColumn(profile, "value");
                break;
            default:
                throw new InvalidOperationException(
                    $"Device '{profile.DeviceId}' parser type '{profile.Parser.Type}' is not implemented in this slice."
                );
        }
    }

    private static void RequireParserColumn(DeviceProfile profile, string columnName)
    {
        if (!profile.Parser.DelimiterTable.Columns.TryGetValue(columnName, out string? column)
            || string.IsNullOrWhiteSpace(column))
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires parser.delimiterTable.columns.{columnName}."
            );
        }
    }

    private static void RequireFixedColumn(DeviceProfile profile, string columnName)
    {
        if (!profile.Parser.FixedColumnReport.Columns.TryGetValue(
                columnName,
                out FixedColumnSlice? slice
            )
            || slice is null
            || slice.Start < 0
            || slice.Length < 1)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires parser.fixedColumnReport.columns.{columnName}."
            );
        }
    }

    private static void ValidateOutput(DeviceProfile profile)
    {
        Uri? targetUrl = profile.OutputTargetUrl;
        if (targetUrl is null)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires output.url or mirthTargetUrl."
            );
        }

        if (!targetUrl.IsAbsoluteUri)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires an absolute output target URL."
            );
        }

        OutputTargetType outputType = profile.Output?.Type ?? OutputTargetType.Http;
        if (outputType == OutputTargetType.Http
            && targetUrl.Scheme is not "http" and not "https")
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires an http or https output URL for HTTP output."
            );
        }

        if (outputType == OutputTargetType.FileFolder && targetUrl.Scheme != "file")
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires a file:// folder URL for file-folder output."
            );
        }
    }

    private static void RequireText(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Device profile requires {fieldName}.");
        }
    }

    private static void RequirePort(DeviceProfile profile)
    {
        if (profile.Port is null or < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"Device '{profile.DeviceId}' requires a TCP port between 1 and 65535."
            );
        }
    }
}
