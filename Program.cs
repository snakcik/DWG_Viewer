using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using System.Text;
using System.Collections.Generic;

var builder = WebApplication.CreateBuilder(args);
// Add services to the container.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

var app = builder.Build();

app.UseResponseCompression();
app.UseDefaultFiles();
app.UseStaticFiles();

bool IsTooDark(string color)
{
    if (string.IsNullOrEmpty(color)) return false;
    color = color.ToLower().Trim();
    if (color == "black" || color == "#000" || color == "#000000" || color.Contains("rgb(0,0,0)")) return true;
    if (color == "white" || color == "#fff" || color == "#ffffff") return false;

    try {
        int r = 0, g = 0, b = 0;
        if (color.StartsWith("#")) {
            string hex = color.Substring(1);
            if (hex.Length == 3) hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            if (hex.Length == 6) {
                r = int.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                g = int.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                b = int.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
            }
        } else if (color.StartsWith("rgb")) {
            var match = System.Text.RegularExpressions.Regex.Match(color, @"rgb\((\d+),\s*(\d+),\s*(\d+)\)");
            if (match.Success) {
                r = int.Parse(match.Groups[1].Value);
                g = int.Parse(match.Groups[2].Value);
                b = int.Parse(match.Groups[3].Value);
            }
        } else return false;

        double brightness = 0.299 * r + 0.587 * g + 0.114 * b;
        return brightness < 80;
    } catch { return false; }
}

app.MapPost("/upload", async (IFormFile file) => {
    var logPath = Path.Combine(Directory.GetCurrentDirectory(), "logs.txt");
    Action<string> log = (msg) => {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Console.WriteLine(line);
        try { File.AppendAllText(logPath, line + Environment.NewLine); } catch {}
    };

    log("=== NEW UPLOAD REQUEST STARTED ===");
    if (file == null || file.Length == 0) {
        log("ERROR: No file uploaded.");
        return Results.BadRequest("No file uploaded.");
    }

    log($"Received file: {file.FileName} ({file.Length} bytes)");

    var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "uploads");
    var outputFolder = Path.Combine(Directory.GetCurrentDirectory(), "output");
    
    if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);
    if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

    var fileName = Guid.NewGuid().ToString() + Path.GetExtension(file.FileName);
    var filePath = Path.Combine(uploadsFolder, fileName);
    var outputFileName = fileName.Replace(".dwg", ".svg").Replace(".DWG", ".svg");
    var outputPath = Path.Combine(outputFolder, outputFileName);

    using (var stream = new FileStream(filePath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }

    log($"File saved to: {filePath}");

    // TRIGGER LibreDWG (dwg2svg.exe)
    var exePath = @"C:\Users\SinanYalçın\Ignis\DWG_Viewer\libredwg_tool\dwg2SVG.exe";
    
    log($"Starting conversion with LibreDWG.");
    log($"Command: \"{exePath}\" --mspace \"{filePath}\"");

    try {
        string tempSvgPath = outputPath + ".tmp.svg";
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{exePath}\" --mspace \"{filePath}\" > \"{tempSvgPath}\"\"",
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null) {
            log("ERROR: Failed to start conversion process cmd.exe.");
            return Results.Problem("Failed to start conversion process.");
        }
        
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await errorTask;
        log($"LibreDWG conversion completed. ExitCode: {process.ExitCode}");

        if (!File.Exists(tempSvgPath) || new FileInfo(tempSvgPath).Length == 0)
        {
            log($"CLI-ERR: SVG generation failed or output is empty. STDERR: {error}");
            return Results.Problem("SVG generation failed or file is empty.");
        }
        
        log($"LibreDWG conversion completed. Temp SVG created at: {tempSvgPath} ({new FileInfo(tempSvgPath).Length} bytes)");

        string output = await File.ReadAllTextAsync(tempSvgPath);
        File.Delete(tempSvgPath);

        log("Read temp SVG content. Validating <svg tag...");
        if (!string.IsNullOrEmpty(output) && output.Contains("<svg"))
        {
            var swOpt = System.Diagnostics.Stopwatch.StartNew();
            long originalSize = output.Length;
            log($"SVG is valid. Starting optimization... (Original Size: {originalSize} bytes)");
            // --- ROBUST OPTIMIZATION START ---
            // 1. Basic structure cleanup (Ensuring main group and styles)
            var finalSvg = output;
            
            // SUPER SAFE MINIFICATION (Reduces 93MB -> ~30MB)
            // 1. Remove all XML comments
            finalSvg = System.Text.RegularExpressions.Regex.Replace(finalSvg, @"<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            // 2. Reduce decimal precision from 6+ digits to 2 digits (e.g. 1909.218561 -> 1909.21)
            finalSvg = System.Text.RegularExpressions.Regex.Replace(finalSvg, @"(\.\d{2})\d+", "$1");
            // 3. Remove excess whitespace/newlines between tags
            finalSvg = System.Text.RegularExpressions.Regex.Replace(finalSvg, @">\s+<", "><");

            var preservedElements = new StringBuilder();
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            bool foundCoords = false;

            int svgStart = finalSvg.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
            if (svgStart < 0) {
                log("ERROR: <svg tag found via Contains but IndexOf failed (Invalid SVG).");
                return Results.Problem("Invalid SVG");
            }
            
            int bodyStart = finalSvg.IndexOf(">", svgStart) + 1;
            int bodyEnd = finalSvg.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
            if (bodyEnd < bodyStart) bodyEnd = finalSvg.Length;

            string header = finalSvg.Substring(svgStart, bodyStart - svgStart);
            bool hasExistingViewBox = System.Text.RegularExpressions.Regex.IsMatch(header, @"viewBox=""[^""]+""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            log($"SVG parsed. Header length: {header.Length}, Body length: {bodyEnd - bodyStart}, hasExistingViewBox: {hasExistingViewBox}. Scanning tags...");
            
            // Fast Tag-by-Tag Scan
            int current = bodyStart;
            bool inDefs = false;
            int tagCount = 0;
            int mergedTagCount = 0;
            var hyperlinks = new HashSet<string>();

            string currentPathAttrs = null;
            StringBuilder currentPathData = new StringBuilder();

            // Helpers for Path Merging
            Func<string, string> getPathAttrs = (tag) => {
                var attrs = new List<string>();
                var matches = System.Text.RegularExpressions.Regex.Matches(tag, @"\s([a-zA-Z0-9\-]+)=""([^""]*)""", System.Text.RegularExpressions.RegexOptions.Singleline);
                foreach (System.Text.RegularExpressions.Match m in matches) {
                    string name = m.Groups[1].Value.ToLower();
                    if (name == "x1" || name == "y1" || name == "x2" || name == "y2" || name == "points" || name == "d" || name == "id") continue;
                    attrs.Add($"{name}=\"{m.Groups[2].Value}\"");
                }
                attrs.Sort();
                return string.Join(" ", attrs);
            };

            Func<string, string> getPathData = (tag) => {
                string lower = tag.ToLower();
                if (lower.StartsWith("<line")) {
                    var mx1 = System.Text.RegularExpressions.Regex.Match(tag, @"x1=""([^""]+)""");
                    var my1 = System.Text.RegularExpressions.Regex.Match(tag, @"y1=""([^""]+)""");
                    var mx2 = System.Text.RegularExpressions.Regex.Match(tag, @"x2=""([^""]+)""");
                    var my2 = System.Text.RegularExpressions.Regex.Match(tag, @"y2=""([^""]+)""");
                    if (mx1.Success && my1.Success && mx2.Success && my2.Success)
                        return $"M{mx1.Groups[1].Value} {my1.Groups[1].Value} L{mx2.Groups[1].Value} {my2.Groups[1].Value}";
                } else if (lower.StartsWith("<polyline") || lower.StartsWith("<polygon")) {
                    var mp = System.Text.RegularExpressions.Regex.Match(tag, @"points=""([^""]+)""");
                    if (mp.Success) return "M " + mp.Groups[1].Value;
                } else if (lower.StartsWith("<path")) {
                    var md = System.Text.RegularExpressions.Regex.Match(tag, @"\sd=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.Singleline);
                    if (!md.Success) md = System.Text.RegularExpressions.Regex.Match(tag, @"<path\s+d=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.Singleline);
                    if (md.Success) return md.Groups[1].Value;
                }
                return "";
            };

            Action flushPath = () => {
                if (currentPathData.Length > 0) {
                     preservedElements.Append($"<path {currentPathAttrs} d=\"{currentPathData}\"/>");
                     currentPathData.Clear();
                     currentPathAttrs = null;
                     mergedTagCount++;
                }
            };
            
            while (current < bodyEnd) {
                int nextTagStart = finalSvg.IndexOf('<', current);
                if (nextTagStart < 0 || nextTagStart >= bodyEnd) break;
                
                int nextTagEnd = -1;
                if (nextTagStart + 4 < bodyEnd && finalSvg.Substring(nextTagStart, 4) == "<!--") {
                    nextTagEnd = finalSvg.IndexOf("-->", nextTagStart);
                    if (nextTagEnd >= 0) nextTagEnd += 2; // Point to '>' in -->
                } else {
                    bool tQuo = false;
                    for (int i = nextTagStart; i < bodyEnd; i++) {
                        if (finalSvg[i] == '"') tQuo = !tQuo;
                        if (finalSvg[i] == '>' && !tQuo) { nextTagEnd = i; break; }
                    }
                }
                if (nextTagEnd < 0) break;

                string tagContent = finalSvg.Substring(nextTagStart, nextTagEnd - nextTagStart + 1);
                current = nextTagEnd + 1;

                if (tagContent.StartsWith("<defs", StringComparison.OrdinalIgnoreCase)) { 
                    flushPath(); inDefs = true; preservedElements.Append(tagContent); continue; 
                }
                if (tagContent.StartsWith("</defs", StringComparison.OrdinalIgnoreCase)) { 
                    flushPath(); inDefs = false; preservedElements.Append(tagContent); continue; 
                }
                if (tagContent.StartsWith("</")) {
                    flushPath(); preservedElements.Append(tagContent); continue;
                }

                string tagLower = tagContent.ToLower();
                
                // STRIP ID AND CLIP-PATH FOR PERFORMANCE & RENDERING STABILITY
                bool keepId = inDefs || tagLower.StartsWith("<text") || tagLower.StartsWith("<a");
                if (!keepId) {
                    tagContent = System.Text.RegularExpressions.Regex.Replace(tagContent, @"\sid=""[^""]*""", "");
                }
                // Strip clip-path which often causes flickering or disappearing lines when panning large drawings
                tagContent = System.Text.RegularExpressions.Regex.Replace(tagContent, @"\sclip-path=""[^""]*""", "");

                // Fix <text> and <a> content
                if (tagLower.StartsWith("<text") && !tagLower.EndsWith("/>")) {
                     int endText = finalSvg.IndexOf("</text>", current, StringComparison.OrdinalIgnoreCase);
                     if (endText > 0) { tagContent += finalSvg.Substring(current, endText - current + 7); current = endText + 7; }
                } else if (tagLower.StartsWith("<a") && !tagLower.EndsWith("/>")) {
                     int endA = finalSvg.IndexOf("</a>", current, StringComparison.OrdinalIgnoreCase);
                     if (endA > 0) { tagContent += finalSvg.Substring(current, endA - current + 4); current = endA + 4; }
                }
                
                // Coordinate Scan for ViewBox (Matches integers and decimals)
                if (!inDefs && !hasExistingViewBox) {
                    var coords = System.Text.RegularExpressions.Regex.Matches(tagContent, @"(-?\d+(?:\.\d+)?)");
                    foreach (System.Text.RegularExpressions.Match m in coords) {
                        if (double.TryParse(m.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double n)) {
                            if (n < minX) minX = n; if (n > maxX) maxX = n;
                            if (n < minY) minY = n; if (n > maxY) maxY = n;
                            foundCoords = true;
                        }
                    }
                }

                // Process colors and add data attributes (Even in defs, as many elements are defined there as symbols)
                string colorUpdatedTag = tagContent;
                bool isMorphed = false;
                var colorMatches = System.Text.RegularExpressions.Regex.Matches(tagContent, @"(stroke|fill)[:=]\s*""?\s*(#[0-9a-fA-F]{3,6}|rgb\([^)]+\)|[a-zA-Z]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match match in colorMatches) {
                    string attrType = match.Groups[1].Value.ToLower();
                    string c = match.Groups[2].Value.Trim().ToLower();
                    string origMatch = match.Value;
                    if (c == "none") continue;
                    string replacementColor = c;
                    if (IsTooDark(c)) replacementColor = "#cccccc";
                    if (replacementColor != c) {
                        string newAttr = origMatch.Replace(match.Groups[2].Value, replacementColor);
                        colorUpdatedTag = colorUpdatedTag.Replace(origMatch, newAttr);
                        isMorphed = true;
                    }
                    string dataAttr = $"data-original-{attrType}=\"{replacementColor}\"";
                    if (!colorUpdatedTag.Contains(dataAttr)) {
                        int spaceIdx = colorUpdatedTag.IndexOf(' ');
                        if (spaceIdx > 0) colorUpdatedTag = colorUpdatedTag.Insert(spaceIdx, $" {dataAttr}");
                        else {
                            int closeIdx = colorUpdatedTag.IndexOf('>');
                            if (closeIdx > 0) colorUpdatedTag = colorUpdatedTag.Insert(closeIdx, $" {dataAttr}");
                        }
                        isMorphed = true;
                    }
                }
                if (isMorphed && !colorUpdatedTag.Contains("class=\"color-group\"")) {
                     int spaceIdx = colorUpdatedTag.IndexOf(' ');
                     if (spaceIdx > 0) colorUpdatedTag = colorUpdatedTag.Insert(spaceIdx, " class=\"color-group\"");
                     else {
                         int closeIdx = colorUpdatedTag.IndexOf('>');
                         if (closeIdx > 0) colorUpdatedTag = colorUpdatedTag.Insert(closeIdx, " class=\"color-group\"");
                     }
                }
                tagContent = colorUpdatedTag;

                // --- MERGE LOGIC ---
                // Re-calculate tagLower after color processing
                tagLower = tagContent.ToLower();
                bool isMergeable = !inDefs && (tagLower.StartsWith("<path") || tagLower.StartsWith("<line") || tagLower.StartsWith("<polyline") || tagLower.StartsWith("<polygon"));
                
                if (isMergeable) {
                    string attrs = getPathAttrs(tagContent);
                    string geom = getPathData(tagContent);
                    
                    if (attrs == currentPathAttrs && currentPathData.Length < 50000) {
                        currentPathData.Append(" ").Append(geom);
                    } else {
                        flushPath();
                        currentPathAttrs = attrs;
                        currentPathData.Append(geom);
                    }
                } else {
                    flushPath();
                    
                    if (tagLower.StartsWith("<a")) {
                        var hrefMatch = System.Text.RegularExpressions.Regex.Match(tagContent, @"(?:xlink:)?href=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                        if (hrefMatch.Success) {
                            hyperlinks.Add(hrefMatch.Groups[1].Value);
                        }
                    }
                    
                    preservedElements.Append(tagContent);
                    mergedTagCount++;
                }
                tagCount++;
            }
            flushPath();
            log($"[OPTI] Çizim tamamlandı: {tagCount} obje işlendi, {mergedTagCount} birleştirilmiş grup, {hyperlinks.Count} hiperlink bulundu.");

            // --- 3. RECONSTRUCT ---
            var finalBody = new StringBuilder();
            finalBody.AppendLine("<g id=\"main-content\">");
            finalBody.Append(preservedElements.ToString());
            finalBody.AppendLine("</g>");

            if (!hasExistingViewBox && foundCoords && minX != double.MaxValue) {
                double w = maxX - minX; double h = maxY - minY;
                double padding = Math.Max(w, h) * 0.05;
                string nv = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2} {1:F2} {2:F2} {3:F2}", minX - padding, minY - padding, w + 2 * padding, h + 2 * padding);
                header = System.Text.RegularExpressions.Regex.Replace(header, @"viewBox=""[^""]*""", $"viewBox=\"{nv}\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                log($"Calculated new ViewBox: {nv}");
            } else if (hasExistingViewBox) {
                log("Reusing existing ViewBox from LibreDWG output.");
            } else {
                log("WARNING: No valid coordinates found for ViewBox calculation. Kept original.");
            }
            header = System.Text.RegularExpressions.Regex.Replace(header, @"\swidth=""[^""]*""", " width=\"100%\"");
            header = System.Text.RegularExpressions.Regex.Replace(header, @"\sheight=""[^""]*""", " height=\"100%\"");

            // HARDWARE ACCELERATION CSS + VISIBILITY FIX (Use non-scaling-stroke for CAD-like thin lines at any zoom)
            string baseStyle = "<style>svg { overflow: hidden !important; background-color: #0f0f13 !important; } #main-content { will-change: transform; } path, polyline, line, circle, rect, ellipse, polygon { vector-effect: non-scaling-stroke !important; pointer-events: none; stroke-width: 1px !important; } text { fill: #ffffff !important; font-family: 'Segoe UI', Arial, sans-serif; font-weight: bold; }</style>";
            
            finalSvg = header + "<defs>" + baseStyle + "</defs>" + finalBody.ToString() + "</svg>";

            await File.WriteAllTextAsync(outputPath, finalSvg);
            swOpt.Stop();
            long finalSize = new FileInfo(outputPath).Length;
            log($"SUCCESS: Optimization complete in {swOpt.ElapsedMilliseconds}ms. Final size: {finalSize} bytes (Original: {originalSize}).");
            return Results.Ok(new { 
                success = true, 
                svgUrl = $"/output/{outputFileName}",
                hyperlinks = hyperlinks.OrderBy(x => x).ToList()
            });
        }
        else {
            log($"ERROR: SVG not found and stdout was empty or invalid. First 50 chars: {(output.Length > 50 ? output.Substring(0,50) : output)}... STDERR: {error}");
            return Results.Problem($"Conversion failed: {error}");
        }
    }
    catch (Exception ex) {
        log($"EXCEPTION CAUGHT: {ex.Message}\nStack Trace: {ex.StackTrace}");
        return Results.Problem($"Exec error: {ex.Message}");
    }
}).DisableAntiforgery();

app.MapGet("/logs", async () => {
    var logPath = Path.Combine(Directory.GetCurrentDirectory(), "logs.txt");
    if (File.Exists(logPath)) return Results.Text(await File.ReadAllTextAsync(logPath));
    return Results.NotFound("No logs available yet. Upload a DWG first.");
});

app.MapGet("/clear-logs", () => {
    var logPath = Path.Combine(Directory.GetCurrentDirectory(), "logs.txt");
    if (File.Exists(logPath)) File.Delete(logPath);
    return Results.Ok("Logs cleared.");
});

// Serve the output folder
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        Path.Combine(Directory.GetCurrentDirectory(), "output")),
    RequestPath = "/output"
});

app.Run();

