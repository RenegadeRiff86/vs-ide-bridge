#include <charconv>
#include <cstdint>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <string_view>

namespace
{
struct EnvelopeInfo
{
    std::string command;
    std::string summary;
    std::string errorCode;
    bool success = false;
    bool hasSuccess = false;
    std::int64_t count = -1;
    bool hasErrors = false;
    bool hasHasErrors = false;
    bool hasWarnings = false;
    bool hasHasWarnings = false;
};

class Parser
{
public:
    explicit Parser(std::string_view text)
        : m_text(text)
    {
    }

    EnvelopeInfo ParseEnvelope()
    {
        EnvelopeInfo info;
        SkipWhitespace();
        ParseTopLevelObject(info);
        SkipWhitespace();
        if (!IsAtEnd())
        {
            throw std::runtime_error("Unexpected trailing content.");
        }

        if (!info.hasSuccess)
        {
            throw std::runtime_error("Missing Success field.");
        }

        return info;
    }

private:
    void ParseTopLevelObject(EnvelopeInfo& info)
    {
        Expect('{');
        SkipWhitespace();
        if (TryConsume('}'))
        {
            return;
        }

        while (true)
        {
            const std::string key = ParseString();
            SkipWhitespace();
            Expect(':');
            SkipWhitespace();

            if (key == "Command")
            {
                info.command = ParseString();
            }
            else if (key == "Success")
            {
                info.success = ParseBool();
                info.hasSuccess = true;
            }
            else if (key == "Summary")
            {
                info.summary = ParseString();
            }
            else if (key == "Error")
            {
                ParseError(info);
            }
            else if (key == "Data")
            {
                ParseData(info);
            }
            else
            {
                SkipValue();
            }

            SkipWhitespace();
            if (TryConsume('}'))
            {
                return;
            }

            Expect(',');
            SkipWhitespace();
        }
    }

    void ParseError(EnvelopeInfo& info)
    {
        if (TryConsumeLiteral("null"))
        {
            return;
        }

        Expect('{');
        SkipWhitespace();
        if (TryConsume('}'))
        {
            return;
        }

        while (true)
        {
            const std::string key = ParseString();
            SkipWhitespace();
            Expect(':');
            SkipWhitespace();

            if (key == "Code")
            {
                info.errorCode = ParseNullableString();
            }
            else
            {
                SkipValue();
            }

            SkipWhitespace();
            if (TryConsume('}'))
            {
                return;
            }

            Expect(',');
            SkipWhitespace();
        }
    }

    void ParseData(EnvelopeInfo& info)
    {
        if (TryConsumeLiteral("null"))
        {
            return;
        }

        Expect('{');
        SkipWhitespace();
        if (TryConsume('}'))
        {
            return;
        }

        while (true)
        {
            const std::string key = ParseString();
            SkipWhitespace();
            Expect(':');
            SkipWhitespace();

            if (key == "count")
            {
                info.count = ParseInteger();
            }
            else if (key == "hasErrors")
            {
                info.hasErrors = ParseBool();
                info.hasHasErrors = true;
            }
            else if (key == "hasWarnings")
            {
                info.hasWarnings = ParseBool();
                info.hasHasWarnings = true;
            }
            else
            {
                SkipValue();
            }

            SkipWhitespace();
            if (TryConsume('}'))
            {
                return;
            }

            Expect(',');
            SkipWhitespace();
        }
    }

    std::string ParseNullableString()
    {
        if (TryConsumeLiteral("null"))
        {
            return {};
        }

        return ParseString();
    }

    std::string ParseString()
    {
        Expect('"');
        std::string result;

        while (!IsAtEnd())
        {
            const char ch = Consume();
            if (ch == '"')
            {
                return result;
            }

            if (ch != '\\')
            {
                result.push_back(ch);
                continue;
            }

            if (IsAtEnd())
            {
                throw std::runtime_error("Incomplete escape sequence.");
            }

            const char escaped = Consume();
            switch (escaped)
            {
            case '"':
            case '\\':
            case '/':
                result.push_back(escaped);
                break;
            case 'b':
                result.push_back('\b');
                break;
            case 'f':
                result.push_back('\f');
                break;
            case 'n':
                result.push_back('\n');
                break;
            case 'r':
                result.push_back('\r');
                break;
            case 't':
                result.push_back('\t');
                break;
            case 'u':
                AppendUtf8(ParseHex16(), result);
                break;
            default:
                throw std::runtime_error("Unsupported escape sequence.");
            }
        }

        throw std::runtime_error("Unterminated string literal.");
    }

    bool ParseBool()
    {
        if (TryConsumeLiteral("true"))
        {
            return true;
        }

        if (TryConsumeLiteral("false"))
        {
            return false;
        }

        throw std::runtime_error("Expected boolean value.");
    }

    std::int64_t ParseInteger()
    {
        const std::size_t start = m_position;
        if (Peek() == '-')
        {
            Consume();
        }

        if (!std::isdigit(static_cast<unsigned char>(Peek())))
        {
            throw std::runtime_error("Expected integer value.");
        }

        while (!IsAtEnd() && std::isdigit(static_cast<unsigned char>(Peek())))
        {
            Consume();
        }

        const std::string_view token = m_text.substr(start, m_position - start);
        std::int64_t value = 0;
        const auto [ptr, ec] = std::from_chars(token.data(), token.data() + token.size(), value);
        if (ec != std::errc() || ptr != token.data() + token.size())
        {
            throw std::runtime_error("Invalid integer value.");
        }

        return value;
    }

    void SkipValue()
    {
        SkipWhitespace();
        if (IsAtEnd())
        {
            throw std::runtime_error("Unexpected end of input.");
        }

        switch (Peek())
        {
        case '{':
            SkipObject();
            return;
        case '[':
            SkipArray();
            return;
        case '"':
            ParseString();
            return;
        case 't':
            if (TryConsumeLiteral("true"))
            {
                return;
            }
            break;
        case 'f':
            if (TryConsumeLiteral("false"))
            {
                return;
            }
            break;
        case 'n':
            if (TryConsumeLiteral("null"))
            {
                return;
            }
            break;
        default:
            SkipNumber();
            return;
        }

        throw std::runtime_error("Unsupported JSON value.");
    }

    void SkipObject()
    {
        Expect('{');
        SkipWhitespace();
        if (TryConsume('}'))
        {
            return;
        }

        while (true)
        {
            ParseString();
            SkipWhitespace();
            Expect(':');
            SkipWhitespace();
            SkipValue();
            SkipWhitespace();
            if (TryConsume('}'))
            {
                return;
            }

            Expect(',');
            SkipWhitespace();
        }
    }

    void SkipArray()
    {
        Expect('[');
        SkipWhitespace();
        if (TryConsume(']'))
        {
            return;
        }

        while (true)
        {
            SkipValue();
            SkipWhitespace();
            if (TryConsume(']'))
            {
                return;
            }

            Expect(',');
            SkipWhitespace();
        }
    }

    void SkipNumber()
    {
        const std::size_t start = m_position;
        if (Peek() == '-')
        {
            Consume();
        }

        if (IsAtEnd())
        {
            throw std::runtime_error("Unexpected end while parsing number.");
        }

        if (Peek() == '0')
        {
            Consume();
        }
        else
        {
            if (!std::isdigit(static_cast<unsigned char>(Peek())))
            {
                throw std::runtime_error("Expected number.");
            }

            while (!IsAtEnd() && std::isdigit(static_cast<unsigned char>(Peek())))
            {
                Consume();
            }
        }

        if (!IsAtEnd() && Peek() == '.')
        {
            Consume();
            while (!IsAtEnd() && std::isdigit(static_cast<unsigned char>(Peek())))
            {
                Consume();
            }
        }

        if (!IsAtEnd() && (Peek() == 'e' || Peek() == 'E'))
        {
            Consume();
            if (!IsAtEnd() && (Peek() == '+' || Peek() == '-'))
            {
                Consume();
            }

            while (!IsAtEnd() && std::isdigit(static_cast<unsigned char>(Peek())))
            {
                Consume();
            }
        }

        if (m_position == start)
        {
            throw std::runtime_error("Expected number.");
        }
    }

    std::uint16_t ParseHex16()
    {
        if (m_position + 4 > m_text.size())
        {
            throw std::runtime_error("Incomplete unicode escape.");
        }

        std::uint16_t value = 0;
        for (int i = 0; i < 4; ++i)
        {
            const char ch = Consume();
            value <<= 4;
            if (ch >= '0' && ch <= '9')
            {
                value |= static_cast<std::uint16_t>(ch - '0');
            }
            else if (ch >= 'a' && ch <= 'f')
            {
                value |= static_cast<std::uint16_t>(10 + ch - 'a');
            }
            else if (ch >= 'A' && ch <= 'F')
            {
                value |= static_cast<std::uint16_t>(10 + ch - 'A');
            }
            else
            {
                throw std::runtime_error("Invalid unicode escape.");
            }
        }

        return value;
    }

    static void AppendUtf8(std::uint16_t codePoint, std::string& output)
    {
        if (codePoint <= 0x7F)
        {
            output.push_back(static_cast<char>(codePoint));
            return;
        }

        if (codePoint <= 0x7FF)
        {
            output.push_back(static_cast<char>(0xC0 | ((codePoint >> 6) & 0x1F)));
            output.push_back(static_cast<char>(0x80 | (codePoint & 0x3F)));
            return;
        }

        output.push_back(static_cast<char>(0xE0 | ((codePoint >> 12) & 0x0F)));
        output.push_back(static_cast<char>(0x80 | ((codePoint >> 6) & 0x3F)));
        output.push_back(static_cast<char>(0x80 | (codePoint & 0x3F)));
    }

    void SkipWhitespace()
    {
        while (!IsAtEnd())
        {
            const char ch = Peek();
            if (ch != ' ' && ch != '\r' && ch != '\n' && ch != '\t')
            {
                return;
            }

            ++m_position;
        }
    }

    bool TryConsume(char ch)
    {
        if (!IsAtEnd() && Peek() == ch)
        {
            ++m_position;
            return true;
        }

        return false;
    }

    bool TryConsumeLiteral(std::string_view literal)
    {
        if (m_text.substr(m_position, literal.size()) == literal)
        {
            m_position += literal.size();
            return true;
        }

        return false;
    }

    void Expect(char ch)
    {
        if (IsAtEnd() || Peek() != ch)
        {
            throw std::runtime_error("Unexpected token.");
        }

        ++m_position;
    }

    char Peek() const
    {
        return IsAtEnd() ? '\0' : m_text[m_position];
    }

    char Consume()
    {
        if (IsAtEnd())
        {
            throw std::runtime_error("Unexpected end of input.");
        }

        return m_text[m_position++];
    }

    bool IsAtEnd() const
    {
        return m_position >= m_text.size();
    }

    std::string_view m_text;
    std::size_t m_position = 0;
};

constexpr std::size_t kUtf8BomThirdByteIndex = 2;
constexpr int kExitCodeError = 2;

std::string ReadFileUtf8(const std::string& path)
{
    std::ifstream input(path, std::ios::binary);
    if (!input)
    {
        throw std::runtime_error("Unable to open input file.");
    }

    std::string text((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
    if (text.size() >= 3 &&
        static_cast<unsigned char>(text[0]) == 0xEF &&
        static_cast<unsigned char>(text[1]) == 0xBB &&
        static_cast<unsigned char>(text[kUtf8BomThirdByteIndex]) == 0xBF)
    {
        text.erase(0, 3);
    }

    return text;
}

std::string EscapeValue(std::string_view value)
{
    std::string escaped;
    escaped.reserve(value.size());

    for (const char ch : value)
    {
        switch (ch)
        {
        case '\\':
            escaped += "\\\\";
            break;
        case '\r':
            escaped += "\\r";
            break;
        case '\n':
            escaped += "\\n";
            break;
        case '\t':
            escaped += "\\t";
            break;
        default:
            escaped.push_back(ch);
            break;
        }
    }

    return escaped;
}

void PrintField(std::string_view key, std::string_view value)
{
    std::cout << key << '=' << EscapeValue(value) << '\n';
}

void PrintUsage()
{
    std::cerr << "Usage: IdeBridgeJsonProbe --input <path-to-command-envelope.json>\n";
}

} // namespace

int main(int argc, char* argv[])
{
    try
    {
        std::string inputPath;
        for (int i = 1; i < argc; ++i)
        {
            const std::string_view arg = argv[i];
            if (arg == "--input" && i + 1 < argc)
            {
                inputPath = argv[++i];
            }
            else if (arg == "--help" || arg == "-h" || arg == "/?")
            {
                PrintUsage();
                return 0;
            }
            else
            {
                PrintUsage();
                return kExitCodeError;
            }
        }

        if (inputPath.empty())
        {
            PrintUsage();
            return kExitCodeError;
        }

        const std::string text = ReadFileUtf8(inputPath);
        const EnvelopeInfo info = Parser(text).ParseEnvelope();

        PrintField("command", info.command);
        PrintField("success", info.success ? "true" : "false");
        PrintField("summary", info.summary);
        PrintField("error_code", info.errorCode);
        if (info.count >= 0)
        {
            PrintField("count", std::to_string(info.count));
        }

        if (info.hasHasErrors)
        {
            PrintField("has_errors", info.hasErrors ? "true" : "false");
        }

        if (info.hasHasWarnings)
        {
            PrintField("has_warnings", info.hasWarnings ? "true" : "false");
        }

        return info.success ? 0 : 1;
    }
    catch (const std::exception& ex)
    {
        std::cerr << "IdeBridgeJsonProbe: " << ex.what() << '\n';
        return kExitCodeError;
    }
}
