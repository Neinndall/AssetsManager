using System;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using AssetsManager.Services.Core;
using Jsbeautifier;

namespace AssetsManager.Services.Formatting
{
    public sealed class JsBeautifierService
    {
        private readonly LogService _logService;
        private const string INDENT = "    ";

        public JsBeautifierService(LogService logService)
        {
            _logService = logService;
        }

        public async Task<string> BeautifyAsync(string jsContent)
        {
            if (string.IsNullOrWhiteSpace(jsContent)) return string.Empty;

            // Ejecutamos en segundo plano para que la UI nunca se congele
            return await Task.Run(() => BeautifyInternal(jsContent));
        }

        private string BeautifyInternal(string jsContent)
        {
            try
            {
                // PASO 1: ¿Es un objeto de datos? (Lo más común en LoL)
                if (TryFormatAsData(jsContent, out string dataFormatted))
                {
                    return dataFormatted;
                }

                // Para archivos grandes (bundles o scripts minificados > 150KB), Jsbeautifier
                // escala de forma cuadrática/exponencial (bloqueos de minutos) y se desincroniza
                // en expresiones regulares modernas o arrays anidados de templates.
                // QuickFormat procesa archivos de cualquier tamaño en milisegundos sin bloqueos.
                if (jsContent.Length <= 150_000)
                {
                    // PASO 2: Intentar formatear con Jsbeautifier para archivos pequeños/medianos
                    try
                    {
                        var options = new BeautifierOptions
                        {
                            IndentSize = 4,
                            IndentChar = ' ',
                            KeepArrayIndentation = false,
                            KeepFunctionIndentation = false,
                            BraceStyle = BraceStyle.Collapse
                        };

                        var beautifier = new Beautifier(options);
                        string formatted = beautifier.Beautify(jsContent);
                        if (!string.IsNullOrWhiteSpace(formatted) && !HasExcessiveLineLength(formatted, 1000))
                        {
                            return formatted;
                        }
                        else
                        {
                            _logService.LogWarning($"[JS BEAUTIFIER] Jsbeautifier output was empty or exceeded safe line limits. Falling back to QuickFormat.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.LogWarning($"[JS BEAUTIFIER] Jsbeautifier failed: {ex.Message}. Falling back to QuickFormat.");
                    }
                }

                // PASO 3: Formateador lineal de alto rendimiento (Failsafe)
                return QuickFormat(jsContent);
            }
            catch
            {
                return jsContent; // Ante la duda, devolver original
            }
        }

        private bool TryFormatAsData(string content, out string formatted)
        {
            formatted = null;
            string trimmed = content.Trim();
            int firstBrace = trimmed.IndexOf('{');
            int lastBrace = trimmed.LastIndexOf('}');

            if (firstBrace == -1 || lastBrace == -1 || lastBrace <= firstBrace) return false;

            try
            {
                string jsonPart = trimmed.Substring(firstBrace, lastBrace - firstBrace + 1);
                // Newtonsoft es muy permisivo y rápido
                var obj = JsonConvert.DeserializeObject(jsonPart);
                if (obj != null)
                {
                    string prettyJson = JsonConvert.SerializeObject(obj, Newtonsoft.Json.Formatting.Indented);
                    string prefix = trimmed.Substring(0, firstBrace);
                    string suffix = trimmed.Substring(lastBrace + 1);
                    formatted = prefix + prettyJson + suffix;
                    return true;
                }
            }
            catch
            {
                // Es completamente normal que esto falle si el .js contiene funciones reales o lógica JS 
                // en lugar de puro JSON.
            }
            return false;
        }

        private static bool HasExcessiveLineLength(string text, int maxAllowedLength)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int currentLen = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n' || c == '\r')
                {
                    currentLen = 0;
                }
                else
                {
                    currentLen++;
                    if (currentLen > maxAllowedLength)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private string QuickFormat(string code)
        {
            if (string.IsNullOrEmpty(code)) return string.Empty;

            var sb = new StringBuilder(code.Length + 16384);
            int indent = 0;
            int currentLineLength = 0;
            const string indentStr = INDENT;
            const int preferredMaxLineLength = 100;
            const int hardMaxLineLength = 400;
            const int maxIndentLevel = 8;

            void AppendIndent()
            {
                int effectiveIndent = Math.Clamp(indent, 0, maxIndentLevel);
                for (int k = 0; k < effectiveIndent; k++)
                {
                    sb.Append(indentStr);
                    currentLineLength += indentStr.Length;
                }
            }

            void AppendNewline()
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != '\n')
                {
                    sb.Append('\n');
                    currentLineLength = 0;
                    AppendIndent();
                }
            }

            char GetLastSignificantChar()
            {
                for (int k = sb.Length - 1; k >= 0; k--)
                {
                    char sc = sb[k];
                    if (!char.IsWhiteSpace(sc)) return sc;
                }
                return '\0';
            }

            string GetLastWord()
            {
                int end = sb.Length - 1;
                while (end >= 0 && char.IsWhiteSpace(sb[end])) end--;
                if (end < 0) return string.Empty;
                int start = end;
                while (start >= 0 && (char.IsLetterOrDigit(sb[start]) || sb[start] == '$' || sb[start] == '_')) start--;
                return sb.ToString(start + 1, end - start);
            }

            bool CanBeRegexPrefix(char lastChar, string lastWord)
            {
                if (lastChar == '\0') return true;
                if (lastChar == '=' || lastChar == '(' || lastChar == '[' || lastChar == ',' ||
                    lastChar == ':' || lastChar == '?' || lastChar == ';' || lastChar == '!' ||
                    lastChar == '&' || lastChar == '|' || lastChar == '{' || lastChar == '}' ||
                    lastChar == '~' || lastChar == '^' || lastChar == '*' || lastChar == '+' ||
                    lastChar == '-' || lastChar == '%' || lastChar == '<' || lastChar == '>' ||
                    lastChar == '\n')
                {
                    return true;
                }
                if (!string.IsNullOrEmpty(lastWord))
                {
                    return lastWord == "return" || lastWord == "case" || lastWord == "throw" ||
                           lastWord == "delete" || lastWord == "typeof" || lastWord == "void" ||
                           lastWord == "else" || lastWord == "do" || lastWord == "in" ||
                           lastWord == "instanceof" || lastWord == "of" || lastWord == "yield" ||
                           lastWord == "await";
                }
                return false;
            }

            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];

                // 1. Strings: ", ', `
                if (c == '"' || c == '\'' || c == '`')
                {
                    char quote = c;
                    sb.Append(quote);
                    currentLineLength++;
                    i++;
                    while (i < code.Length)
                    {
                        char sc = code[i];
                        sb.Append(sc);
                        currentLineLength++;
                        if (sc == '\n') currentLineLength = 0;

                        if (sc == '\\')
                        {
                            if (i + 1 < code.Length)
                            {
                                i++;
                                sb.Append(code[i]);
                                currentLineLength++;
                            }
                        }
                        else if (sc == quote)
                        {
                            break;
                        }
                        else if (currentLineLength > hardMaxLineLength && (sc == ' ' || sc == ','))
                        {
                            AppendNewline();
                        }
                        i++;
                    }
                    continue;
                }

                // 2. Comments: // and /*
                if (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
                {
                    while (i < code.Length && code[i] != '\n' && code[i] != '\r')
                    {
                        sb.Append(code[i]);
                        currentLineLength++;
                        i++;
                    }
                    if (i < code.Length)
                    {
                        sb.Append(code[i]);
                        currentLineLength = 0;
                        AppendIndent();
                    }
                    continue;
                }

                if (c == '/' && i + 1 < code.Length && code[i + 1] == '*')
                {
                    sb.Append("/*");
                    currentLineLength += 2;
                    i += 2;
                    while (i < code.Length - 1 && !(code[i] == '*' && code[i + 1] == '/'))
                    {
                        char mc = code[i];
                        sb.Append(mc);
                        if (mc == '\n') currentLineLength = 0; else currentLineLength++;
                        i++;
                    }
                    if (i < code.Length) { sb.Append(code[i]); currentLineLength++; }
                    if (i + 1 < code.Length) { sb.Append(code[i + 1]); currentLineLength++; i++; }
                    continue;
                }

                // 3. Regular Expression Literal /.../
                if (c == '/')
                {
                    char lastChar = GetLastSignificantChar();
                    string lastWord = GetLastWord();
                    if (CanBeRegexPrefix(lastChar, lastWord))
                    {
                        sb.Append('/');
                        currentLineLength++;
                        i++;
                        bool inCharClass = false;
                        while (i < code.Length)
                        {
                            char rc = code[i];
                            sb.Append(rc);
                            currentLineLength++;

                            if (rc == '\\')
                            {
                                if (i + 1 < code.Length)
                                {
                                    i++;
                                    sb.Append(code[i]);
                                    currentLineLength++;
                                }
                            }
                            else if (rc == '[')
                            {
                                inCharClass = true;
                            }
                            else if (rc == ']' && inCharClass)
                            {
                                inCharClass = false;
                            }
                            else if (rc == '/' && !inCharClass)
                            {
                                // Append flags if any
                                while (i + 1 < code.Length && char.IsLetter(code[i + 1]))
                                {
                                    i++;
                                    sb.Append(code[i]);
                                    currentLineLength++;
                                }
                                break;
                            }
                            i++;
                        }
                        continue;
                    }
                }

                // 4. Structural tokens
                switch (c)
                {
                    case '{':
                        sb.Append('{');
                        if (indent < maxIndentLevel) indent++;
                        AppendNewline();
                        break;

                    case '}':
                        if (indent > 0) indent--;
                        AppendNewline();
                        sb.Append('}');
                        currentLineLength++;
                        break;

                    case '[':
                        sb.Append('[');
                        currentLineLength++;
                        break;

                    case ']':
                        sb.Append(']');
                        currentLineLength++;
                        break;

                    case ';':
                        sb.Append(';');
                        AppendNewline();
                        break;

                    case ',':
                        sb.Append(',');
                        if (currentLineLength > preferredMaxLineLength)
                        {
                            AppendNewline();
                        }
                        else
                        {
                            sb.Append(' ');
                            currentLineLength++;
                        }
                        break;

                    case '\n':
                    case '\r':
                    case '\t':
                        // Collapse whitespace
                        break;

                    case ' ':
                        if (sb.Length > 0 && sb[sb.Length - 1] != '\n' && sb[sb.Length - 1] != ' ')
                        {
                            sb.Append(' ');
                            currentLineLength++;
                        }
                        break;

                    default:
                        sb.Append(c);
                        currentLineLength++;
                        if (currentLineLength > hardMaxLineLength && (c == '&' || c == '|' || c == '+' || c == '-' || c == '(' || c == ')'))
                        {
                            AppendNewline();
                        }
                        break;
                }
            }

            return sb.ToString().Trim();
        }
    }
}
