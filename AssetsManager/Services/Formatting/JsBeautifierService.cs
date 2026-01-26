using System;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AssetsManager.Services.Formatting
{
    public sealed class JsBeautifierService
    {
        private const string INDENT = "    ";
        private const int MAX_SIZE_FOR_FORMATTING = 3 * 1024 * 1024; // 3MB

        public async Task<string> BeautifyAsync(string jsContent)
        {
            if (string.IsNullOrWhiteSpace(jsContent)) return string.Empty;

            // Si es un archivo masivo, no lo formateamos para evitar que AvalonEdit mate la app
            if (jsContent.Length > MAX_SIZE_FOR_FORMATTING) return jsContent;

            // Ejecutamos en segundo plano para que la UI nunca se congele
            return await Task.Run(() => BeautifyInternal(jsContent));
        }

        private string BeautifyInternal(string jsContent)
        {
            try
            {
                // PASO 1: ¿Es un objeto de datos? (Lo más común en LoL)
                // Si detectamos que es un "var x = { ... };", usamos el motor JSON
                if (TryFormatAsData(jsContent, out string dataFormatted))
                {
                    return dataFormatted;
                }

                // PASO 2: Formateador lineal de alto rendimiento (Estilo JSTool)
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
            catch { }
            return false;
        }

        private string QuickFormat(string code)
        {
            // Este algoritmo camina una sola vez por el string (O(n))
            // Es lo que permite que JSTool sea tan rápido
            var sb = new StringBuilder(code.Length + 1024);
            int indent = 0;
            bool inString = false;
            char stringChar = '\0';

            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];

                // Manejo básico de strings para no romper nada dentro de comillas
                if ((c == '"' || c == '\'' || c == '`') && (i == 0 || code[i - 1] != '\\'))
                {
                    if (!inString) { inString = true; stringChar = c; }
                    else if (c == stringChar) inString = false;
                    sb.Append(c);
                    continue;
                }

                if (inString) { sb.Append(c); continue; }

                // Lógica de formateo estilo JSTool
                switch (c)
                {
                    case '{':
                    case '[':
                        sb.Append(c);
                        sb.Append('\n');
                        indent++;
                        AppendIndent(sb, indent);
                        break;
                    case '}':
                    case ']':
                        indent = Math.Max(0, indent - 1);
                        if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                        AppendIndent(sb, indent);
                        sb.Append(c);
                        break;
                    case ';':
                        sb.Append(c);
                        sb.Append('\n');
                        AppendIndent(sb, indent);
                        break;
                    case ',':
                        sb.Append(c);
                        sb.Append('\n');
                        AppendIndent(sb, indent);
                        break;
                    case '\n':
                    case '\r':
                    case '\t':
                        // Saltamos espacios en blanco originales para poner los nuestros
                        break;
                    case ' ':
                        // Solo añadimos espacio si no es al principio de la línea
                        if (sb.Length > 0 && sb[sb.Length - 1] != '\n' && sb[sb.Length - 1] != ' ')
                            sb.Append(c);
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }

            return sb.ToString().Trim();
        }

        private void AppendIndent(StringBuilder sb, int count)
        {
            for (int i = 0; i < count; i++) sb.Append(INDENT);
        }
    }
}