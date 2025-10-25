using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Collections.Generic;
using NUglify;
using NUglify.JavaScript;

namespace AssetsManager.Services.Formatting
{
    public sealed class JsBeautifierService
    {
        private readonly CodeSettings _beautifySettings;
        private readonly CodeSettings _fallbackSettings;

        public JsBeautifierService()
        {
            // Configuración principal optimizada
            _beautifySettings = new CodeSettings
            {
                MinifyCode = false,
                OutputMode = OutputMode.MultipleLines,
                Indent = "  ",
                LocalRenaming = LocalRenaming.KeepAll,
                PreserveFunctionNames = true,
                PreserveImportantComments = true,
                RemoveUnneededCode = false,
                StripDebugStatements = false,
                CollapseToLiteral = false,
                ReorderScopeDeclarations = false,
                RemoveFunctionExpressionNames = false
            };

            // Configuración más permisiva para fallback
            _fallbackSettings = new CodeSettings
            {
                MinifyCode = false,
                OutputMode = OutputMode.MultipleLines,
                Indent = "  ",
                LocalRenaming = LocalRenaming.KeepAll,
                PreserveFunctionNames = true,
                PreserveImportantComments = true,
                RemoveUnneededCode = false,
                StripDebugStatements = false,
                CollapseToLiteral = false,
                ReorderScopeDeclarations = false,
                RemoveFunctionExpressionNames = false,
                AlwaysEscapeNonAscii = false
            };
        }

        /// <summary>
        /// Embellece código JavaScript de forma asíncrona con estrategia multi-nivel.
        /// </summary>
        public async Task<string> BeautifyAsync(string jsContent)
        {
            if (string.IsNullOrWhiteSpace(jsContent))
                return string.Empty;

            // Para archivos muy grandes, usar Task.Run para no bloquear el hilo UI
            if (jsContent.Length > 50000)
            {
                return await Task.Run(() => BeautifyInternal(jsContent));
            }
            
            return BeautifyInternal(jsContent);
        }

        private string BeautifyInternal(string jsContent)
        {
            try
            {
                // Paso 1: Intentar con NUglify SIN preprocesamiento (más seguro)
                var result = Uglify.Js(jsContent, _beautifySettings);
                
                if (!result.HasErrors && !string.IsNullOrEmpty(result.Code))
                {
                    return PostprocessBeautified(result.Code);
                }
                
                // Paso 2: Intentar con configuración más permisiva
                try
                {
                    result = Uglify.Js(jsContent, _fallbackSettings);
                    
                    // Incluso si hay errores menores, intentar usar el resultado si tiene código
                    if (!string.IsNullOrEmpty(result.Code))
                    {
                        return PostprocessBeautified(result.Code);
                    }
                }
                catch
                {
                    // Continuar al fallback manual
                }
                
                // Paso 3: Fallback con beautificación manual mejorada
                return ApplyAdvancedBeautification(jsContent);
            }
            catch (Exception)
            {
                // Fallback final
                return ApplyAdvancedBeautification(jsContent);
            }
        }

        /// <summary>
        /// Postprocesa el código beautificado por NUglify
        /// </summary>
        private string PostprocessBeautified(string beautified)
        {
            var result = beautified;
            
            // Mejorar espaciado en operadores
            result = Regex.Replace(result, @"(\w+)([=!<>+\-*/%])(\w+)", "$1 $2 $3");
            
            // Mejorar espaciado en funciones
            result = Regex.Replace(result, @"function\s*\(", "function (");
            result = Regex.Replace(result, @"\)\s*\{", ") {");
            
            // Separar elementos de arrays largos
            result = Regex.Replace(result, @",(\w)", ", $1");
            
            return result;
        }

        /// <summary>
        /// Beautificación avanzada manual para casos complejos
        /// </summary>
        private string ApplyAdvancedBeautification(string jsContent)
        {
            if (string.IsNullOrWhiteSpace(jsContent))
                return string.Empty;

            var result = jsContent;
            
            // Transformaciones mejoradas con mejor detección de contexto
            result = ApplyContextAwareFormatting(result);
            
            // Aplicar indentación inteligente
            return ApplyIntelligentIndentation(result);
        }

        /// <summary>
        /// Formateo inteligente que considera el contexto
        /// </summary>
        private string ApplyContextAwareFormatting(string jsContent)
        {
            var result = jsContent;
            
            // Mejorar separación de statements (evitar romper strings)
            result = Regex.Replace(result, @";(?=\s*[a-zA-Z_$])", ";\n");
            
            // Mejorar apertura de bloques
            result = Regex.Replace(result, @"(?<![""])\{(?!\s*[""])", " {\n");
            
            // Mejorar cierre de bloques (evitar dentro de strings)
            result = Regex.Replace(result, @"(?<![""])\}(?!\s*[,;""\]])", "\n}\n");
            
            // Mejorar separación de elementos en arrays/objetos (evitar strings largas base64)
            result = Regex.Replace(result, @",(?=\s*[a-zA-Z_$""\[])(?!.{100,})", ",\n");
            
            // Espaciado en operadores (evitar dentro de strings)
            result = Regex.Replace(result, @"(?<![""])([=!<>+\-*/%])(?![""])", " $1 ");
            
            // Funciones
            result = Regex.Replace(result, @"(\w+)\s*=\s*function\s*\(", "$1 = function(");
            result = Regex.Replace(result, @"function\s*\(", "function (");
            
            // Clases
            result = Regex.Replace(result, @"\bclass\s+(\w+)\s*\{", "class $1 {\n");
            
            return result;
        }

        /// <summary>
        /// Indentación inteligente mejorada
        /// </summary>
        private string ApplyIntelligentIndentation(string jsContent)
        {
            var lines = jsContent.Split(new[] { '\n', '\r' }, StringSplitOptions.None);
            var result = new StringBuilder(jsContent.Length + (lines.Length * 4));
            var indentStack = new Stack<int>();
            var currentIndent = 0;
            const int INDENT_SIZE = 2;
            const int MAX_INDENT = 40; // Aumentado para estructuras profundas de LOL

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                
                if (string.IsNullOrEmpty(trimmed))
                {
                    result.AppendLine();
                    continue;
                }

                // Manejar cierres
                if (IsAdvancedClosingLine(trimmed))
                {
                    if (indentStack.Count > 0)
                        currentIndent = indentStack.Pop();
                    else
                        currentIndent = Math.Max(0, currentIndent - INDENT_SIZE);
                }

                // Aplicar indentación
                var actualIndent = Math.Min(currentIndent, MAX_INDENT);
                result.AppendLine(new string(' ', actualIndent) + trimmed);

                // Manejar aperturas
                if (IsAdvancedOpeningLine(trimmed))
                {
                    indentStack.Push(currentIndent);
                    currentIndent += INDENT_SIZE;
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Detección avanzada de líneas de cierre
        /// </summary>
        private bool IsAdvancedClosingLine(string line)
        {
            var closingPatterns = new[]
            {
                @"^\}",
                @"^\]",
                @"^\);",
                @"^\});",
                @"^\},",
                @"^\],",
                @"^});",
                @"^\)\s*\{?\s*$"
            };

            return closingPatterns.Any(pattern => Regex.IsMatch(line, pattern));
        }

        /// <summary>
        /// Detección avanzada de líneas de apertura
        /// </summary>
        private bool IsAdvancedOpeningLine(string line)
        {
            var openingPatterns = new[]
            {
                @"\{\s*$",
                @"\[\s*$",
                @"class\s+\w+\s*\{\s*$", // Clases
                @"function\s*\([^)]*\)\s*\{\s*$",
                @"\w+\s*\([^)]*\)\s*\{\s*$", // Métodos de clase
                @"=>\s*\{\s*$",
                @"if\s*\([^)]*\)\s*\{\s*$",
                @"for\s*\([^)]*\)\s*\{\s*$",
                @"while\s*\([^)]*\)\s*\{\s*$",
                @"try\s*\{\s*$",
                @"catch\s*\([^)]*\)\s*\{\s*$",
                @"finally\s*\{\s*$"
            };

            return openingPatterns.Any(pattern => Regex.IsMatch(line, pattern));
        }
    }
}