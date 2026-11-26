package dev.pocketagent;

import android.text.Html;
import android.text.Spanned;
import android.text.method.LinkMovementMethod;
import android.widget.TextView;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

/** Small, dependency-free Markdown renderer for native conversation messages. */
final class MarkdownText {
  private MarkdownText() {}

  static void apply(TextView view, String markdown) {
    view.setText(render(markdown));
    view.setTextIsSelectable(true);
    view.setLinksClickable(true);
    view.setMovementMethod(LinkMovementMethod.getInstance());
  }

  static Spanned render(String markdown) {
    return Html.fromHtml(toHtml(markdown == null ? "" : markdown), Html.FROM_HTML_MODE_LEGACY);
  }

  private static String toHtml(String markdown) {
    String[] lines = markdown.replace("\r\n", "\n").replace('\r', '\n').split("\n", -1);
    StringBuilder html = new StringBuilder(markdown.length() + 64);
    int index = 0;
    while (index < lines.length) {
      if (lines[index].trim().isEmpty()) {
        index++;
        continue;
      }
      String fence = fence(lines[index]);
      if (fence != null) {
        index++;
        StringBuilder code = new StringBuilder();
        while (index < lines.length && fence(lines[index]) == null) {
          if (code.length() > 0) code.append('\n');
          code.append(lines[index++]);
        }
        if (index < lines.length) index++;
        html.append("<pre><code>").append(escape(code.toString())).append("</code></pre>");
        continue;
      }
      if (heading(lines[index]) != null) {
        Heading heading = heading(lines[index++]);
        html.append("<h").append(heading.level).append('>')
            .append(inline(heading.text)).append("</h").append(heading.level).append('>');
        continue;
      }
      if (isThematicBreak(lines[index])) {
        html.append("<p>────────</p>");
        index++;
        continue;
      }
      if (isTable(lines, index)) {
        index = appendTable(lines, index, html);
        continue;
      }
      if (quote(lines[index]) != null) {
        html.append("<blockquote>");
        while (index < lines.length && quote(lines[index]) != null) {
          if (html.charAt(html.length() - 1) != '>') html.append("<br>");
          html.append(inline(quote(lines[index++])));
        }
        html.append("</blockquote>");
        continue;
      }
      if (listMarker(lines[index]) != null) {
        index = appendList(lines, index, html);
        continue;
      }

      StringBuilder paragraph = new StringBuilder(lines[index++]);
      while (index < lines.length && !lines[index].trim().isEmpty() && !startsBlock(lines, index)) {
        paragraph.append(' ').append(lines[index++].trim());
      }
      html.append("<p>").append(inline(paragraph.toString())).append("</p>");
    }
    return html.toString();
  }

  private static boolean startsBlock(String[] lines, int index) {
    return fence(lines[index]) != null
        || heading(lines[index]) != null
        || isThematicBreak(lines[index])
        || isTable(lines, index)
        || quote(lines[index]) != null
        || listMarker(lines[index]) != null;
  }

  private static String fence(String line) {
    String trimmed = line.trim();
    if (trimmed.startsWith("```") || trimmed.startsWith("~~~")) return trimmed.substring(0, 3);
    return null;
  }

  private static Heading heading(String line) {
    int level = 0;
    while (level < line.length() && level < 6 && line.charAt(level) == '#') level++;
    if (level == 0 || level == line.length() || !Character.isWhitespace(line.charAt(level))) return null;
    String text = line.substring(level).trim();
    while (text.endsWith("#")) text = text.substring(0, text.length() - 1).trim();
    return new Heading(level, text);
  }

  private static boolean isThematicBreak(String line) {
    String compact = line.replace(" ", "").replace("\t", "");
    return compact.length() >= 3
        && ((compact.replace("-", "").isEmpty())
            || (compact.replace("*", "").isEmpty())
            || (compact.replace("_", "").isEmpty()));
  }

  private static String quote(String line) {
    String trimmed = line.trim();
    if (!trimmed.startsWith(">")) return null;
    return trimmed.length() > 1 && trimmed.charAt(1) == ' ' ? trimmed.substring(2) : trimmed.substring(1);
  }

  private static ListMarker listMarker(String line) {
    int indent = 0;
    while (indent < line.length() && (line.charAt(indent) == ' ' || line.charAt(indent) == '\t')) indent++;
    if (indent >= line.length()) return null;
    char marker = line.charAt(indent);
    if ((marker == '-' || marker == '*' || marker == '+')
        && indent + 1 < line.length()
        && Character.isWhitespace(line.charAt(indent + 1))) {
      return new ListMarker(indent, false, line.substring(indent + 2).trim());
    }
    int cursor = indent;
    while (cursor < line.length() && Character.isDigit(line.charAt(cursor))) cursor++;
    if (cursor > indent
        && cursor + 1 < line.length()
        && (line.charAt(cursor) == '.' || line.charAt(cursor) == ')')
        && Character.isWhitespace(line.charAt(cursor + 1))) {
      return new ListMarker(indent, true, line.substring(cursor + 2).trim());
    }
    return null;
  }

  private static int appendList(String[] lines, int index, StringBuilder html) {
    ListMarker first = listMarker(lines[index]);
    int indent = first.indent;
    boolean ordered = first.ordered;
    html.append(ordered ? "<ol>" : "<ul>");
    while (index < lines.length) {
      ListMarker marker = listMarker(lines[index]);
      if (marker == null || marker.indent != indent || marker.ordered != ordered) break;
      html.append("<li>").append(inline(marker.text));
      index++;
      if (index < lines.length) {
        ListMarker nested = listMarker(lines[index]);
        if (nested != null && nested.indent > indent) index = appendList(lines, index, html);
      }
      html.append("</li>");
    }
    html.append(ordered ? "</ol>" : "</ul>");
    return index;
  }

  private static boolean isTable(String[] lines, int index) {
    return index + 1 < lines.length
        && lines[index].contains("|")
        && tableSeparator(lines[index + 1]);
  }

  private static int appendTable(String[] lines, int index, StringBuilder html) {
    List<List<String>> rows = new ArrayList<>();
    rows.add(tableCells(lines[index++]));
    index++;
    while (index < lines.length && lines[index].contains("|") && !lines[index].trim().isEmpty()) {
      rows.add(tableCells(lines[index++]));
    }
    int columns = rows.stream().mapToInt(List::size).max().orElse(0);
    int[] widths = new int[columns];
    for (List<String> row : rows)
      for (int column = 0; column < row.size(); column++) widths[column] = Math.max(widths[column], row.get(column).length());
    html.append("<pre>");
    for (int rowIndex = 0; rowIndex < rows.size(); rowIndex++) {
      List<String> row = rows.get(rowIndex);
      for (int column = 0; column < columns; column++) {
        if (column > 0) html.append("  ");
        String cell = column < row.size() ? row.get(column) : "";
        html.append(escape(cell));
        for (int padding = cell.length(); padding < widths[column]; padding++) html.append(' ');
      }
      if (rowIndex == 0) {
        html.append("\n");
        for (int column = 0; column < columns; column++) {
          if (column > 0) html.append("  ");
          for (int padding = 0; padding < widths[column]; padding++) html.append('-');
        }
      }
      if (rowIndex < rows.size() - 1) html.append('\n');
    }
    html.append("</pre>");
    return index;
  }

  private static boolean tableSeparator(String line) {
    List<String> cells = tableCells(line);
    if (cells.isEmpty()) return false;
    for (String cell : cells) if (!cell.matches(":?-{3,}:?")) return false;
    return true;
  }

  private static List<String> tableCells(String line) {
    String value = line.trim();
    if (value.startsWith("|")) value = value.substring(1);
    if (value.endsWith("|")) value = value.substring(0, value.length() - 1);
    List<String> cells = new ArrayList<>();
    StringBuilder cell = new StringBuilder();
    boolean escaped = false;
    for (int i = 0; i < value.length(); i++) {
      char current = value.charAt(i);
      if (current == '|' && !escaped) {
        cells.add(cell.toString().trim());
        cell.setLength(0);
      } else {
        if (current == '\\' && !escaped) escaped = true;
        else escaped = false;
        cell.append(current);
      }
    }
    cells.add(cell.toString().trim());
    return cells;
  }

  private static String inline(String source) {
    StringBuilder output = new StringBuilder(source.length() + 16);
    for (int index = 0; index < source.length();) {
      if (source.startsWith("![", index)) {
        int labelEnd = source.indexOf("](", index + 2);
        int linkEnd = labelEnd < 0 ? -1 : source.indexOf(')', labelEnd + 2);
        if (linkEnd >= 0) {
          index = linkEnd + 1;
          continue;
        }
      }
      if (source.charAt(index) == '`') {
        int end = source.indexOf('`', index + 1);
        if (end > index + 1) {
          output.append("<code>").append(escape(source.substring(index + 1, end))).append("</code>");
          index = end + 1;
          continue;
        }
      }
      if (source.charAt(index) == '[') {
        int labelEnd = source.indexOf("](", index + 1);
        int linkEnd = labelEnd < 0 ? -1 : source.indexOf(')', labelEnd + 2);
        if (linkEnd > labelEnd) {
          String label = source.substring(index + 1, labelEnd);
          String target = source.substring(labelEnd + 2, linkEnd).trim();
          int title = target.indexOf(' ');
          if (title >= 0) target = target.substring(0, title);
          String safe = safeLink(target);
          output.append(safe == null ? inline(label) : "<a href=\"" + escapeAttribute(safe) + "\">" + inline(label) + "</a>");
          index = linkEnd + 1;
          continue;
        }
      }
      if (source.charAt(index) == '<') {
        int end = source.indexOf('>', index + 1);
        if (end > index) {
          String target = source.substring(index + 1, end);
          String safe = safeLink(target);
          if (safe != null) {
            output.append("<a href=\"").append(escapeAttribute(safe)).append("\">")
                .append(escape(safe)).append("</a>");
            index = end + 1;
            continue;
          }
        }
      }
      String delimiter = source.startsWith("**", index) ? "**" : source.startsWith("__", index) ? "__" : null;
      if (delimiter != null) {
        int end = source.indexOf(delimiter, index + 2);
        if (end > index + 2) {
          output.append("<strong>").append(inline(source.substring(index + 2, end))).append("</strong>");
          index = end + 2;
          continue;
        }
      }
      if (source.charAt(index) == '*' || source.charAt(index) == '_') {
        char delimiterChar = source.charAt(index);
        int end = source.indexOf(delimiterChar, index + 1);
        if (end > index + 1 && !Character.isWhitespace(source.charAt(index + 1))) {
          output.append("<em>").append(inline(source.substring(index + 1, end))).append("</em>");
          index = end + 1;
          continue;
        }
      }
      output.append(escape(String.valueOf(source.charAt(index++))));
    }
    return output.toString();
  }

  private static String safeLink(String value) {
    String lower = value.toLowerCase(Locale.ROOT);
    return lower.startsWith("https://") || lower.startsWith("http://") ? value : null;
  }

  private static String escape(String value) {
    return value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;");
  }

  private static String escapeAttribute(String value) {
    return escape(value).replace("\"", "&quot;");
  }

  private record Heading(int level, String text) {}

  private record ListMarker(int indent, boolean ordered, String text) {}
}
