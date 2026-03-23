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
            // --- 0. PRE-SCAN FOR HYPERLINKS WITH COORDINATES ---
            var hyperlinksData = new List<object>();
            var seenLinks = new HashSet<string>();
            var fullLinkPattern = @"<a\s+[^>]*?xlink:href=[""']([^""']+)[""'][^>]*?>(.*?)</a>";
            var linkMatches = System.Text.RegularExpressions.Regex.Matches(finalSvg, fullLinkPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            
            foreach (System.Text.RegularExpressions.Match lm in linkMatches) {
                string val = lm.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(val) || val.StartsWith("#") || val.StartsWith("data:")) continue;
                if (!seenLinks.Add(val)) continue;

                string inner = lm.Groups[2].Value;
                var coordMatch = System.Text.RegularExpressions.Regex.Match(inner, @"d=[""']M\s*(-?\d+\.?\d*)\s*(-?\d+\.?\d*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                double lx = 0, ly = 0;
                if (coordMatch.Success) {
                    double.TryParse(coordMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lx);
                    double.TryParse(coordMatch.Groups[2].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out ly);
                }
                hyperlinksData.Add(new { text = val, x = lx, y = ly });
            }
            log($"[HYPER] Raw SVG içinde {hyperlinksData.Count} benzersiz link ve koordinat bulundu.");

            // Fast Tag-by-Tag Scan
            int current = bodyStart;
            bool inDefs = false;
            var segmentsInCurrentPath = new HashSet<long>();
            int tagCount = 0;
            int mergedTagCount = 0;
            int dedupedTagCount = 0;
            var dedupeSet = new HashSet<string>();

            string? currentPathAttrs = null;
            StringBuilder currentPathData = new StringBuilder();

            // Style to Class mapping
            var styleToClass = new Dictionary<string, string>();
            int classCounter = 0;
            var cssRules = new StringBuilder();

            // Helpers for Path Merging & Deduplication
            Func<string, string?> getPathAttrs = (tag) => {
                var attrs = new List<string>();
                var matches = System.Text.RegularExpressions.Regex.Matches(tag, @"\s([a-zA-Z0-9\-:]+)=""([^""]*)""", System.Text.RegularExpressions.RegexOptions.Singleline);
                foreach (System.Text.RegularExpressions.Match m in matches) {
                    string name = m.Groups[1].Value.ToLower();
                    if (name == "x1" || name == "y1" || name == "x2" || name == "y2" || name == "points" || name == "d" || name == "id" || name == "style") continue;
                    attrs.Add($"{m.Groups[1].Value}=\"{m.Groups[2].Value}\"");
                }
                
                // Extract style to global CSS
                var styleMatch = System.Text.RegularExpressions.Regex.Match(tag, @"style=""([^""]*)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (styleMatch.Success) {
                    string styleVal = styleMatch.Groups[1].Value;
                    
                    // -- WIREFRAME MODE: Skip solid fills without strokes --
                    bool hasFill = styleVal.Contains("fill:") && !styleVal.Contains("fill:none");
                    bool hasStroke = styleVal.Contains("stroke:") && !styleVal.Contains("stroke:none");
                    if (hasFill && !hasStroke) return null; // Skip this object entirely
                    
                    if (!styleToClass.TryGetValue(styleVal, out string? cls)) {
                        cls = $"c{classCounter++}";
                        styleToClass[styleVal] = cls;
                        cssRules.AppendLine($".{cls} {{ {styleVal.Replace("fill:", "fill-old:").Replace(";fill", ";fill-old")} ; fill: none !important; }}");
                    }
                    attrs.Add($"class=\"{cls}\"");
                }

                attrs.Sort();
                return string.Join(" ", attrs);
            };

            Func<string, string> roundCoords = (data) => {
                // Precision 0.0001 (4 decimals) is hyper-precise for all CAD scales
                return System.Text.RegularExpressions.Regex.Replace(data, @"(-?\d+\.\d{4})\d+", "$1");
            };

            Func<string, string> getPathData = (tag) => {
                string lower = tag.ToLower();
                string d = "";
                if (lower.StartsWith("<line")) {
                    var mx1 = System.Text.RegularExpressions.Regex.Match(tag, @"x1=""([^""]+)""");
                    var my1 = System.Text.RegularExpressions.Regex.Match(tag, @"y1=""([^""]+)""");
                    var mx2 = System.Text.RegularExpressions.Regex.Match(tag, @"x2=""([^""]+)""");
                    var my2 = System.Text.RegularExpressions.Regex.Match(tag, @"y2=""([^""]+)""");
                    if (mx1.Success && my1.Success && mx2.Success && my2.Success)
                        d = $"M{mx1.Groups[1].Value} {my1.Groups[1].Value} L{mx2.Groups[1].Value} {my2.Groups[1].Value}";
                } else if (lower.StartsWith("<polyline") || lower.StartsWith("<polygon")) {
                    var mp = System.Text.RegularExpressions.Regex.Match(tag, @"points=""([^""]+)""");
                    if (mp.Success) d = "M " + mp.Groups[1].Value;
                } else if (lower.StartsWith("<path")) {
                    var md = System.Text.RegularExpressions.Regex.Match(tag, @"\sd=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (md.Success) d = md.Groups[1].Value;
                }
                
                if (string.IsNullOrEmpty(d)) return "";
                return roundCoords(d);
            };
            
            Action flushPath = () => {
                if (currentPathData.Length > 0) {
                    string combinedPath = currentPathData.ToString().Trim();
                    string dedupeKey = (currentPathAttrs ?? "") + "|" + combinedPath;
                    
                    if (!dedupeSet.Contains(dedupeKey)) {
                        preservedElements.Append($"<path {currentPathAttrs} d=\"{combinedPath}\"/>");
                        dedupeSet.Add(dedupeKey);
                    } else {
                        dedupedTagCount++;
                    }
                    currentPathData.Clear();
                    currentPathAttrs = null;
                }
            };
            
            while (current < bodyEnd) {
                int nextTagStart = finalSvg.IndexOf('<', current);
                if (nextTagStart > current) {
                    string gap = finalSvg.Substring(current, nextTagStart - current);
                    if (!string.IsNullOrWhiteSpace(gap)) {
                        flushPath(); preservedElements.Append(gap);
                    }
                }
                
                if (nextTagStart < 0 || nextTagStart >= bodyEnd) break;
                
                int nextTagEnd = -1;
                if (nextTagStart + 4 < bodyEnd && finalSvg.Substring(nextTagStart, 4) == "<!--") {
                    nextTagEnd = finalSvg.IndexOf("-->", nextTagStart);
                    if (nextTagEnd >= 0) nextTagEnd += 2; 
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
                
                if (!inDefs && !tagLower.StartsWith("<text") && !tagLower.StartsWith("<a")) {
                    tagContent = System.Text.RegularExpressions.Regex.Replace(tagContent, @"\sid=""[^""]*""", "");
                }
                tagContent = System.Text.RegularExpressions.Regex.Replace(tagContent, @"\sclip-path=""[^""]*""", "");

                // -- Fix multi-line tags (<text>, <a>) --
                if (tagLower.StartsWith("<text") && !tagLower.EndsWith("/>")) {
                     int endText = finalSvg.IndexOf("</text>", current, StringComparison.OrdinalIgnoreCase);
                     if (endText > 0) { tagContent += finalSvg.Substring(current, endText - current + 7); current = endText + 7; }
                } else if (tagLower.StartsWith("<a") && !tagLower.EndsWith("/>")) {
                     int endA = finalSvg.IndexOf("</a>", current, StringComparison.OrdinalIgnoreCase);
                     if (endA > 0) { tagContent += finalSvg.Substring(current, endA - current + 4); current = endA + 4; }
                }

                // -- Color Transform --
                string colorUpdatedTag = tagContent;
                bool isMorphed = false;
                var colorMatch = System.Text.RegularExpressions.Regex.Match(tagContent, @"(?:stroke|fill)[:=]\s*""?\s*(#[0-9a-fA-F]{3,6}|rgb\([^)]+\)|[a-zA-Z]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (colorMatch.Success) {
                    string c = colorMatch.Value.Split(new[] {':','='})[1].Trim(' ','"','\'').ToLower();
                    if (c != "none") {
                        if (IsTooDark(c)) {
                            colorUpdatedTag = colorUpdatedTag.Replace(colorMatch.Value, colorMatch.Value.Replace(c, "#cccccc"));
                            isMorphed = true;
                        }
                        if (!colorUpdatedTag.Contains("data-original-")) {
                            string attrType = colorMatch.Value.Contains("stroke") ? "stroke" : "fill";
                            int spaceIdx = colorUpdatedTag.IndexOf(' ');
                            if (spaceIdx > 0) colorUpdatedTag = colorUpdatedTag.Insert(spaceIdx, $" data-original-{attrType}=\"{c}\"");
                            isMorphed = true;
                        }
                    }
                }
                if (isMorphed && !colorUpdatedTag.Contains("class=\"color-group\"")) {
                    int spaceIdx = colorUpdatedTag.IndexOf(' ');
                    if (spaceIdx > 0) colorUpdatedTag = colorUpdatedTag.Insert(spaceIdx, " class=\"color-group\"");
                }
                tagContent = colorUpdatedTag;
                tagLower = tagContent.ToLower();

                // -- Merge & Dedupe --
                bool isMergeable = (tagLower.StartsWith("<path") || tagLower.StartsWith("<line") || tagLower.StartsWith("<polyline") || tagLower.StartsWith("<polygon"));
                if (isMergeable) {
                    tagCount++;
                    string? attrs = getPathAttrs(tagContent);
                    if (attrs == null) { dedupedTagCount++; continue; } // Skip solid-filled entity

                    string data = getPathData(tagContent);
                    if (string.IsNullOrEmpty(data)) { flushPath(); preservedElements.Append(tagContent); continue; }

                    if (attrs != currentPathAttrs || currentPathData.Length > 250000) {
                        flushPath();
                        currentPathAttrs = attrs;
                    }

                    // -- HEURISTIC HATCH DECIMATION (Scanline Filter) --
                    // Split data into segments: M x1 y1 L x2 y2
                    var segMatches = System.Text.RegularExpressions.Regex.Matches(data, @"M\s*(-?\d+\.?\d*)\s*(-?\d+\.?\d*)\s*L\s*(-?\d+\.?\d*)\s*(-?\d+\.?\d*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (segMatches.Count > 0) {
                        foreach (System.Text.RegularExpressions.Match sm in segMatches) {
                            if (double.TryParse(sm.Groups[1].Value, out double x1) && double.TryParse(sm.Groups[2].Value, out double y1) &&
                                double.TryParse(sm.Groups[3].Value, out double x2) && double.TryParse(sm.Groups[4].Value, out double y2)) {
                                
                                // Quantize for spatial comparison (0.5 unit threshold)
                                long qx1 = (long)(Math.Round(x1 * 2) * 10); // * 10 to avoid collisions with 0
                                long qy1 = (long)(Math.Round(y1 * 2) * 10);
                                long qx2 = (long)(Math.Round(x2 * 2) * 10);
                                long qy2 = (long)(Math.Round(y2 * 2) * 10);
                                
                                // Create a unique hash for this segment (independent of direction)
                                long h1 = qx1 ^ (qy1 << 16) ^ (qx2 << 32) ^ (qy2 << 48);
                                long h2 = qx2 ^ (qy2 << 16) ^ (qx1 << 32) ^ (qy1 << 48);
                                long segmentHash = Math.Min(h1, h2);

                                if (segmentsInCurrentPath.Add(segmentHash)) {
                                    if (currentPathData.Length > 0) currentPathData.Append(" ");
                                    currentPathData.Append(sm.Value);
                                    mergedTagCount++;
                                } else {
                                    dedupedTagCount++;
                                }
                            }
                        }
                    } else {
                        // Not a standard M L segment, add it as is but flush to be safe
                        if (currentPathData.Length > 0) currentPathData.Append(" ");
                        currentPathData.Append(data);
                        mergedTagCount++;
                    }
                } else {
                    flushPath();
                    preservedElements.Append(tagContent);
                }
            }
            flushPath();

            // -- Final Coordinate Scan for ViewBox --
            if (!hasExistingViewBox) {
                var coords = System.Text.RegularExpressions.Regex.Matches(preservedElements.ToString(), @"(-?\d+(?:\.\d+)?)");
                foreach (System.Text.RegularExpressions.Match m in coords) {
                    if (double.TryParse(m.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double n)) {
                        if (n < minX) minX = n; if (n > maxX) maxX = n;
                        if (n < minY) minY = n; if (n > maxY) maxY = n;
                        foundCoords = true;
                    }
                }
            }

            log($"[OPTI] Tamamlandı: {tagCount} obje, {mergedTagCount} birleştirme, {dedupedTagCount} kopya silindi, {hyperlinksData.Count} link bulundu.");

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

            // HARDWARE ACCELERATION CSS + DYNAMIC STYLE CLASSES
            string baseStyle = $@"<style>
                svg {{ overflow: hidden !important; }} 
                #main-content {{ will-change: transform; }} 
                path, polyline, line, circle, rect, ellipse, polygon {{ vector-effect: none !important; pointer-events: none; stroke-width: 1px !important; }} 
                text {{ fill: #ffffff !important; font-family: 'Segoe UI', Arial, sans-serif; font-weight: bold; }}
                {cssRules.ToString()}
            </style>";
            
            finalSvg = header + "<defs>" + baseStyle + "</defs>" + finalBody.ToString() + "</svg>";

            await File.WriteAllTextAsync(outputPath, finalSvg);
            swOpt.Stop();
            long finalSize = new FileInfo(outputPath).Length;
            log($"SUCCESS: Optimization complete in {swOpt.ElapsedMilliseconds}ms. Final size: {finalSize} bytes (Original: {originalSize}).");
            return Results.Ok(new { 
                success = true, 
                svgUrl = $"/output/{outputFileName}",
                hyperlinks = hyperlinksData
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

