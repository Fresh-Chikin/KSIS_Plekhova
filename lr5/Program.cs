using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

//  папка, где будут физически лежать файлы
var storageRoot = Path.Combine(Directory.GetCurrentDirectory(), "Storage");

if (!Directory.Exists(storageRoot))
{
    Directory.CreateDirectory(storageRoot);
}

// Обработчик для маршрутов, содержащих путь к файлу или папке
app.MapMethods("{**path}", new[] { "GET", "PUT", "DELETE", "HEAD" }, async (
    HttpContext context,
    string? path,
    [FromHeader(Name = "X-Copy-From")] string? copyFrom) => // Читаем заголовок X-Copy-From если есть
{
    // убираем из пути попытки выйти за пределы папки Storage
    if (string.IsNullOrEmpty(path))
    {
        if (context.Request.Method == HttpMethods.Get)
        {
            await HandleGetDirectoryListing(context, storageRoot, "");
            return;
        }
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Path cannot be empty for this operation.");
        return;
    }

    // Нормализуем путь
    var safePath = path.Replace('\\', '/').TrimStart('/');
    if (safePath.Contains(".."))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Invalid path (.. is not allowed).");
        return;
    }

    var fullLocalPath = Path.GetFullPath(Path.Combine(storageRoot, safePath));

    // проверка на выход за пределы корневой папки 
    if (!fullLocalPath.StartsWith(storageRoot + Path.DirectorySeparatorChar) && fullLocalPath != storageRoot)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Access denied outside of storage root.");
        return;
    }

    // Обрабатываем методы 
    switch (context.Request.Method)
    {
        case "GET":
            if (Directory.Exists(fullLocalPath))
            {
                await HandleGetDirectoryListing(context, storageRoot, safePath);
            }
            else if (File.Exists(fullLocalPath))
            {
                await HandleGetFile(context, fullLocalPath);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
            break;

        case "HEAD":
            if (File.Exists(fullLocalPath))
            {
                await HandleHeadFile(context, fullLocalPath);
            }
            else if (Directory.Exists(fullLocalPath))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.Headers["Content-Type"] = "application/json";
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
            break;

        case "PUT":
            // Копирование
            if (!string.IsNullOrEmpty(copyFrom))
            {
                await HandleCopyFile(context, storageRoot, copyFrom, safePath);
            }
            else
            {
                await HandlePutFile(context, fullLocalPath);
            }
            break;

        case "DELETE":
            await HandleDelete(context, fullLocalPath);
            break;

        default:
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            break;
    }
});

app.Run();

//Обработчики 

async Task HandleGetDirectoryListing(HttpContext context, string root, string relativePath)
{
    var targetPath = string.IsNullOrEmpty(relativePath) ? root : Path.Combine(root, relativePath);

    if (!Directory.Exists(targetPath))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var entries = new List<object>();

    // Добавляем информацию о папках
    foreach (var dir in Directory.GetDirectories(targetPath))
    {
        var dirInfo = new DirectoryInfo(dir);
        entries.Add(new
        {
            Name = dirInfo.Name,
            Type = "directory",
            LastModified = dirInfo.LastWriteTimeUtc
        });
    }

    // Добавляем информацию о файлах
    foreach (var file in Directory.GetFiles(targetPath))
    {
        var fileInfo = new FileInfo(file);
        entries.Add(new
        {
            Name = fileInfo.Name,
            Type = "file",
            Size = fileInfo.Length,
            LastModified = fileInfo.LastWriteTimeUtc
        });
    }

    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(entries, new JsonSerializerOptions { WriteIndented = true });
}

async Task HandleGetFile(HttpContext context, string filePath)
{
    var fileInfo = new FileInfo(filePath);

    // Устанавливаем заголовки: размер и дата изменения
    context.Response.Headers["Content-Length"] = fileInfo.Length.ToString();
    context.Response.Headers["Last-Modified"] = fileInfo.LastWriteTimeUtc.ToString("R"); // RFC1123 формат

    // Определяем Content-Type 
    var contentType = "application/octet-stream";
    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    if (ext == ".txt") contentType = "text/plain";
    else if (ext == ".json") contentType = "application/json";
    else if (ext == ".jpg" || ext == ".jpeg") contentType = "image/jpeg";
    else if (ext == ".png") contentType = "image/png";
    
    context.Response.ContentType = contentType;
    await context.Response.SendFileAsync(filePath);
}

async Task HandleHeadFile(HttpContext context, string filePath)
{
    var fileInfo = new FileInfo(filePath);

    // Устанавливаем заголовки без тела ответа
    context.Response.Headers["Content-Length"] = fileInfo.Length.ToString();
    context.Response.Headers["Last-Modified"] = fileInfo.LastWriteTimeUtc.ToString("R");

    var contentType = "application/octet-stream";
    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    if (ext == ".txt") contentType = "text/plain";
    else if (ext == ".json") contentType = "application/json";
    else if (ext == ".jpg" || ext == ".jpeg") contentType = "image/jpeg";
    else if (ext == ".png") contentType = "image/png";

    context.Response.ContentType = contentType;
    context.Response.StatusCode = StatusCodes.Status200OK;
    }

async Task HandlePutFile(HttpContext context, string filePath)
{
    // Создаем папку, если её нет
    var directory = Path.GetDirectoryName(filePath);
    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
    {
        Directory.CreateDirectory(directory);
    }

    // Записываем тело запроса в файл 
    await using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
    {
        await context.Request.Body.CopyToAsync(fileStream);
    }

    context.Response.StatusCode = StatusCodes.Status200OK; 
    await context.Response.WriteAsync($"File {Path.GetFileName(filePath)} uploaded successfully.");
}

// Копирование
async Task HandleCopyFile(HttpContext context, string root, string sourceRelative, string destRelative)
{
    sourceRelative = sourceRelative.TrimStart('/');
    destRelative = destRelative.TrimStart('/');

    var sourceFull = Path.GetFullPath(Path.Combine(root, sourceRelative));
    var destFull = Path.GetFullPath(Path.Combine(root, destRelative));

    // Проверка, что исходный файл существует
    if (!File.Exists(sourceFull))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync($"Source file '{sourceRelative}' not found.");
        return;
    }

    // Защита от выхода за пределы Storage
    if (!sourceFull.StartsWith(root) || !destFull.StartsWith(root))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    // Создаем папку назначения, если нужно
    var destDir = Path.GetDirectoryName(destFull);
    if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
    {
        Directory.CreateDirectory(destDir);
    }

    // Копируем с перезаписью
    File.Copy(sourceFull, destFull, true);

    context.Response.StatusCode = StatusCodes.Status201Created;
    await context.Response.WriteAsync($"File copied from '{sourceRelative}' to '{destRelative}'.");
}

async Task HandleDelete(HttpContext context, string fullLocalPath)
{
    if (Directory.Exists(fullLocalPath))
    {
        try
        {
            Directory.Delete(fullLocalPath, true); // удалять рекурсивно
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync($"Failed to delete directory: {ex.Message}");
        }
    }
    else if (File.Exists(fullLocalPath))
    {
        try
        {
            File.Delete(fullLocalPath);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync($"Failed to delete file: {ex.Message}");
        }
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }
}