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

                // PASO 2: Intentar formatear de manera profesional con Jsbeautifier (C# Port de js-beautify)
                try
                {
                    var options = new BeautifierOptions
                    {
                        IndentSize = 4,
                        IndentChar = ' ',
                        KeepArrayIndentation = true,
                        KeepFunctionIndentation = false,
                        BraceStyle = BraceStyle.Collapse
                    };

                    var beautifier = new Beautifier(options);
                    string formatted = beautifier.Beautify(jsContent);
                    if (!string.IsNullOrWhiteSpace(formatted))
                    {
                        return formatted;
                    }
                    else
                    {
                        _logService.LogWarning($"[JS BEAUTIFIER] Jsbeautifier returned empty output. Falling back to QuickFormat.");
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogWarning($"[JS BEAUTIFIER] Jsbeautifier failed: {ex.Message}. Falling back to QuickFormat.");
                }

                // PASO 3: Formateador lineal de alto rendimiento (Failsafe)
                // Imprescindible para archivos minificados masivos. Si devolvemos una 
                // única línea gigante, AvalonEdit la trunca y el comparador Diff colapsa.
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

        private string QuickFormat(string code)
        {
            var sb = new StringBuilder(code.Length + 1024);
            int indent = 0;
            bool inString = false;
            char stringChar = '\0';
            int currentLineLength = 0;

            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];

                // Failsafe: Evitar que CUALQUIER línea supere los 5000 caracteres.
                // Si el lexer se desincroniza (ej. comillas dentro de regex complejas),
                // esto garantiza que AvalonEdit jamás crasheará por OOM al intentar
                // renderizar una línea infinita.
                if (currentLineLength > 5000)
                {
                    sb.Append('\n');
                    currentLineLength = 0;
                    inString = false; // Resetear estado por si se quedó atascado
                }

                // 1. Skip single line comments
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
                    }
                    continue;
                }

                // 2. Skip multi-line comments
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
                    if (i + 1 < code.Length) { sb.Append(code[i + 1]); currentLineLength++; }
                    i++;
                    continue;
                }

                // 3. Skip regular expression literals (e.g. /regex/)
                if (c == '/' && !inString)
                {
                    char lastChar = '\0';
                    for (int j = sb.Length - 1; j >= 0; j--)
                    {
                        if (!char.IsWhiteSpace(sb[j]))
                        {
                            lastChar = sb[j];
                            break;
                        }
                    }

                    bool isRegex = lastChar == '\0' || lastChar == '=' || lastChar == '(' || lastChar == '[' || 
                                   lastChar == ',' || lastChar == ':' || lastChar == '?' || lastChar == '&' || 
                                   lastChar == '|' || lastChar == '!' || lastChar == '{' || lastChar == '}' || 
                                   lastChar == ';' || lastChar == '\n' || lastChar == 'n'; // 'n' for return

                    if (isRegex)
                    {
                        sb.Append(c);
                        currentLineLength++;
                        i++;
                        bool inRegexCharClass = false;
                        while (i < code.Length)
                        {
                            char rc = code[i];
                            sb.Append(rc);
                            if (rc == '\n') currentLineLength = 0; else currentLineLength++;
                            
                            if (rc == '\\')
                            {
                                if (i + 1 < code.Length)
                                {
                                    sb.Append(code[i + 1]);
                                    currentLineLength++;
                                    i++;
                                }
                            }
                            else if (rc == '[')
                            {
                                inRegexCharClass = true;
                            }
                            else if (rc == ']')
                            {
                                inRegexCharClass = false;
                            }
                            else if (rc == '/' && !inRegexCharClass)
                            {
                                break;
                            }
                            i++;
                        }
                        continue;
                    }
                }

                // 4. Basic string literal handling
                if (c == '"' || c == '\'' || c == '`')
                {
                    int backslashCount = 0;
                    int tempIndex = i - 1;
                    while (tempIndex >= 0 && code[tempIndex] == '\\')
                    {
                        backslashCount++;
                        tempIndex--;
                    }

                    if (backslashCount % 2 == 0) // No está escapada
                    {
                        if (!inString) { inString = true; stringChar = c; }
                        else if (c == stringChar) inString = false;
                    }
                    
                    sb.Append(c);
                    currentLineLength++;
                    continue;
                }

                if (inString) 
                { 
                    sb.Append(c); 
                    if (c == '\n') currentLineLength = 0; else currentLineLength++;
                    continue; 
                }

                switch (c)
                {
                    case '{':
                        sb.Append(c);
                        sb.Append('\n');
                        currentLineLength = 0;
                        indent++;
                        AppendIndent(sb, indent, ref currentLineLength);
                        break;
                    case '}':
                        indent = Math.Max(0, indent - 1);
                        if (sb.Length > 0 && sb[sb.Length - 1] != '\n') 
                        {
                            sb.Append('\n');
                            currentLineLength = 0;
                        }
                        AppendIndent(sb, indent, ref currentLineLength);
                        sb.Append(c);
                        currentLineLength++;
                        break;
                    case ';':
                        sb.Append(c);
                        sb.Append('\n');
                        currentLineLength = 0;
                        AppendIndent(sb, indent, ref currentLineLength);
                        break;
                    case ',':
                        sb.Append(c);
                        sb.Append(' ');
                        currentLineLength += 2;
                        break;
                    case '\n':
                    case '\r':
                    case '\t':
                        // Ignore original whitespace when outside strings
                        break;
                    case ' ':
                        if (sb.Length > 0 && sb[sb.Length - 1] != '\n' && sb[sb.Length - 1] != ' ')
                        {
                            sb.Append(c);
                            currentLineLength++;
                        }
                        break;
                    default:
                        sb.Append(c);
                        currentLineLength++;
                        break;
                }
            }

            return sb.ToString().Trim();
        }

        private void AppendIndent(StringBuilder sb, int count, ref int currentLineLength)
        {
            for (int i = 0; i < count; i++) 
            {
                sb.Append(INDENT);
                currentLineLength += INDENT.Length;
            }
        }
    }
}
