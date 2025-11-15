using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AssetsManager.Services.Formatting
{
  // Modelos de datos
  public class CSSBlock
  {
    public List<string> Selectors { get; set; } = new List<string>();
    public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
  }

  public class ParseOptions
  {
    public ParseMode Mode { get; set; } = ParseMode.Grouped;
    public bool PrettyPrint { get; set; } = true;
    public bool PreserveComments { get; set; } = false;
  }

  public enum ParseMode
  {
    Grouped,      // Mantiene selectores agrupados
    Individual,   // Separa cada selector
    Hierarchical  // Estructura jerárquica por selector
  }

  // Servicio principal
  public interface ICSSParserService
  {
    string ConvertToJson(string cssContent, ParseOptions options = null);
    Task<string> ConvertToJsonAsync(string cssContent, ParseOptions options = null);
    List<CSSBlock> ParseGrouped(string cssContent);
    Dictionary<string, Dictionary<string, string>> ParseIndividual(string cssContent);
    Dictionary<string, object> ParseHierarchical(string cssContent);
  }

  public class CSSParserService : ICSSParserService
  {
    public Task<string> ConvertToJsonAsync(string cssContent, ParseOptions options = null)
    {
      return Task.Run(() => ConvertToJson(cssContent, options));
    }

    /// <summary>
    /// Convierte CSS a JSON según las opciones especificadas
    /// </summary>
    public string ConvertToJson(string cssContent, ParseOptions options = null)
    {
      if (string.IsNullOrWhiteSpace(cssContent))
        throw new ArgumentException("El contenido CSS no puede estar vacío", nameof(cssContent));

      options ??= new ParseOptions();

      var jsonOptions = new JsonSerializerOptions
      {
        WriteIndented = options.PrettyPrint,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
      };

      object result = options.Mode switch
      {
        ParseMode.Grouped => ParseGrouped(cssContent),
        ParseMode.Individual => ParseIndividual(cssContent),
        ParseMode.Hierarchical => ParseHierarchical(cssContent),
        _ => ParseGrouped(cssContent)
      };

      return JsonSerializer.Serialize(result, jsonOptions);
    }

    /// <summary>
    /// Parsea CSS manteniendo los selectores agrupados
    /// Ideal para: Mantener la estructura original del CSS
    /// </summary>
    public List<CSSBlock> ParseGrouped(string cssContent)
    {
      var result = new List<CSSBlock>();
      var cleanedCss = PreprocessCSS(cssContent);
      var blocks = ExtractCSSBlocks(cleanedCss);

      foreach (var block in blocks)
      {
        var cssBlock = ParseBlock(block);
        if (cssBlock != null && cssBlock.Selectors.Any() && cssBlock.Properties.Any())
        {
          result.Add(cssBlock);
        }
      }

      return result;
    }

    /// <summary>
    /// Parsea CSS separando cada selector individualmente
    /// Ideal para: Búsquedas rápidas por selector específico
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ParseIndividual(string cssContent)
    {
      var result = new Dictionary<string, Dictionary<string, string>>();
      var cleanedCss = PreprocessCSS(cssContent);
      var blocks = ExtractCSSBlocks(cleanedCss);

      foreach (var block in blocks)
      {
        var cssBlock = ParseBlock(block);
        if (cssBlock == null || !cssBlock.Selectors.Any() || !cssBlock.Properties.Any())
          continue;

        foreach (var selector in cssBlock.Selectors)
        {
          if (!result.ContainsKey(selector))
          {
            result[selector] = new Dictionary<string, string>();
          }

          foreach (var prop in cssBlock.Properties)
          {
            result[selector][prop.Key] = prop.Value;
          }
        }
      }

      return result;
    }

    /// <summary>
    /// Parsea CSS creando una estructura jerárquica
    /// Ideal para: Análisis de cascada y herencia de estilos
    /// </summary>
    public Dictionary<string, object> ParseHierarchical(string cssContent)
    {
      var result = new Dictionary<string, object>();
      var individual = ParseIndividual(cssContent);

      foreach (var kvp in individual)
      {
        var selector = kvp.Key;
        var properties = kvp.Value;

        // Dividir el selector en partes jerárquicas
        var parts = SplitSelectorHierarchy(selector);
        AddToHierarchy(result, parts, properties);
      }

      return result;
    }

    // Métodos privados auxiliares

    private string PreprocessCSS(string css)
    {
      // Eliminar comentarios CSS
      css = Regex.Replace(css, @"/\*[\s\S]*?\*/", string.Empty);

      // Normalizar espacios en blanco
      css = Regex.Replace(css, @"\s+", " ");

      return css.Trim();
    }

    private List<string> ExtractCSSBlocks(string css)
    {
      var blocks = new List<string>();
      var currentBlock = "";
      var braceCount = 0;

      foreach (var c in css)
      {
        currentBlock += c;

        if (c == '{')
          braceCount++;
        else if (c == '}')
        {
          braceCount--;
          if (braceCount == 0 && !string.IsNullOrWhiteSpace(currentBlock))
          {
            blocks.Add(currentBlock.Trim());
            currentBlock = "";
          }
        }
      }

      return blocks;
    }

    private CSSBlock ParseBlock(string block)
    {
      var openBraceIndex = block.IndexOf('{');
      if (openBraceIndex == -1)
        return null;

      var selectorsStr = block.Substring(0, openBraceIndex);
      var propertiesStr = block.Substring(openBraceIndex + 1);

      // Remover la llave de cierre
      if (propertiesStr.EndsWith("}"))
        propertiesStr = propertiesStr.Substring(0, propertiesStr.Length - 1);

      var selectors = ParseSelectors(selectorsStr);
      var properties = ParseProperties(propertiesStr);

      return new CSSBlock
      {
        Selectors = selectors,
        Properties = properties
      };
    }

    private List<string> ParseSelectors(string selectorsStr)
    {
      return selectorsStr
          .Split(',')
          .Select(s => CleanSelector(s))
          .Where(s => !string.IsNullOrEmpty(s))
          .Distinct()
          .ToList();
    }

    private Dictionary<string, string> ParseProperties(string propertiesStr)
    {
      var properties = new Dictionary<string, string>();

      var props = propertiesStr
          .Split(';')
          .Select(p => p.Trim())
          .Where(p => !string.IsNullOrWhiteSpace(p));

      foreach (var prop in props)
      {
        var colonIndex = prop.IndexOf(':');
        if (colonIndex == -1)
          continue;

        var key = prop.Substring(0, colonIndex).Trim();
        var value = prop.Substring(colonIndex + 1).Trim();

        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
        {
          properties[key] = value;
        }
      }

      return properties;
    }

    private string CleanSelector(string selector)
    {
      // Normalizar espacios múltiples a uno solo
      selector = Regex.Replace(selector, @"\s+", " ");
      return selector.Trim();
    }

    private List<string> SplitSelectorHierarchy(string selector)
    {
      // Divide el selector por espacios (descendientes) pero mantiene clases/ids juntos
      var parts = new List<string>();
      var current = "";

      foreach (var c in selector)
      {
        if (c == ' ' && !string.IsNullOrEmpty(current))
        {
          parts.Add(current);
          current = "";
        }
        else if (c != ' ')
        {
          current += c;
        }
      }

      if (!string.IsNullOrEmpty(current))
        parts.Add(current);

      return parts;
    }

    private void AddToHierarchy(Dictionary<string, object> root, List<string> parts, Dictionary<string, string> properties)
    {
      if (!parts.Any())
        return;

      var key = parts[0];

      if (parts.Count == 1)
      {
        // Último nivel - agregar propiedades
        if (!root.ContainsKey(key))
        {
          root[key] = new Dictionary<string, object>();
        }

        var node = root[key] as Dictionary<string, object>;
        if (node != null)
        {
          if (!node.ContainsKey("properties"))
          {
            node["properties"] = properties;
          }
          else
          {
            var existingProps = node["properties"] as Dictionary<string, string>;
            if (existingProps != null)
            {
              foreach (var prop in properties)
              {
                existingProps[prop.Key] = prop.Value;
              }
            }
          }
        }
      }
      else
      {
        // Nivel intermedio - continuar recursivamente
        if (!root.ContainsKey(key))
        {
          root[key] = new Dictionary<string, object>();
        }

        var node = root[key] as Dictionary<string, object>;
        if (node != null)
        {
          if (!node.ContainsKey("children"))
          {
            node["children"] = new Dictionary<string, object>();
          }

          var children = node["children"] as Dictionary<string, object>;
          if (children != null)
          {
            AddToHierarchy(children, parts.Skip(1).ToList(), properties);
          }
        }
      }
    }
  }

}
