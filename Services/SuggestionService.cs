using System.IO;
using System.Windows.Media.Imaging;
using FileRecoveryParser.Models;

namespace FileRecoveryParser.Services;

public class SuggestionService : IDisposable
{
    private FaceRecognitionService? _faceService;
    private readonly PersonStore    _store;
    private bool _modelsReady;
    private bool _disposed;

    public SuggestionService(PersonStore store)
    {
        _store = store;
        _store.Load();
    }

    // ── Model readiness ───────────────────────────────────────────────────────

    public async Task EnsureReadyAsync(Action<string> status, CancellationToken ct)
    {
        if (_modelsReady) return;
        await ModelDownloader.EnsureModelsAsync(status, ct);
        _faceService = new FaceRecognitionService(
            ModelDownloader.DetectionModelPath,
            ModelDownloader.RecognitionModelPath);
        _modelsReady = true;
    }

    // ── Main entry point ──────────────────────────────────────────────────────

    public Task<IList<SuggestionResult>> GetSuggestionsAsync(FileRecord file, CancellationToken ct) =>
        file.Category == FileCategory.Image
            ? GetImageSuggestionsAsync(file, ct)
            : Task.FromResult(GetDocumentSuggestions(file));

    // ── Face-tagging API (driven by the image preview UI) ────────────────────

    public record FaceAnalysis(
        System.Windows.Rect Box,
        float[]             Embedding,
        PersonRecord?       MatchedPerson);

    /// <summary>
    /// Detects every face in <paramref name="filePath"/>, computes the embedding
    /// for each, and matches against the persistent PersonStore. Used by the
    /// image-preview face overlay so the user can name each face directly.
    /// Lazily initialises the ONNX face models on first call.
    /// </summary>
    public async Task<IList<FaceAnalysis>> AnalyzeFacesAsync(
        string filePath, CancellationToken ct, Action<string>? status = null)
    {
        status ??= _ => { };

        if (!_modelsReady)
        {
            try { await EnsureReadyAsync(status, ct); }
            catch (Exception ex)
            {
                status($"Face model load failed: {ex.Message}");
                return [];
            }
        }

        if (_faceService is null)
        {
            status("Face service unavailable (model not loaded).");
            return [];
        }

        return await Task.Run<IList<FaceAnalysis>>(() =>
        {
            try
            {
                var faces  = _faceService.DetectFaces(filePath);
                var output = new List<FaceAnalysis>(faces.Count);
                foreach (var f in faces)
                {
                    ct.ThrowIfCancellationRequested();
                    var emb   = _faceService.GetEmbedding(filePath, f);
                    var match = _store.Match(emb);
                    output.Add(new FaceAnalysis(f.Box, emb, match?.Person));
                }
                return output;
            }
            catch (Exception ex)
            {
                status($"Face detection failed: {ex.Message}");
                return [];
            }
        }, ct);
    }

    /// <summary>
    /// Computes a MobileFaceNet embedding from an already-cropped bitmap.
    /// Used by the manual region-tagging flow (where no face detection has
    /// run) and by per-crop scan matching.
    /// </summary>
    public async Task<float[]?> GetEmbeddingFromCropAsync(
        BitmapSource crop, CancellationToken ct, Action<string>? status = null)
    {
        status ??= _ => { };
        if (!_modelsReady)
        {
            try { await EnsureReadyAsync(status, ct); }
            catch { return null; }
        }
        if (_faceService is null) return null;

        return await Task.Run<float[]?>(() =>
        {
            try { return _faceService.GetEmbeddingFromBitmap(crop); }
            catch { return null; }
        }, ct);
    }

    public float[]? GetEmbeddingFromCrop(BitmapSource crop)
    {
        if (_faceService is null) return null;
        try { return _faceService.GetEmbeddingFromBitmap(crop); }
        catch { return null; }
    }

    /// <summary>
    /// Assigns a name to the face whose embedding most closely matches the
    /// supplied one. Creates a new PersonRecord if no existing person matches.
    /// </summary>
    public PersonRecord NameFace(float[] embedding, string name)
    {
        var match  = _store.Match(embedding);
        var person = match?.Person ?? _store.CreatePerson(embedding);
        _store.SetPersonName(person, name);
        return person;
    }

    // ── Image suggestions (face recognition) ─────────────────────────────────

    private async Task<IList<SuggestionResult>> GetImageSuggestionsAsync(FileRecord file, CancellationToken ct)
    {
        if (_faceService is null || !File.Exists(file.FullPath))
            return [];

        return await Task.Run(() =>
        {
            try
            {
                var suggestions = new List<SuggestionResult>();
                var faces = _faceService.DetectFaces(file.FullPath);
                if (faces.Count == 0) return (IList<SuggestionResult>)suggestions;

                // Use the highest-confidence face for matching
                var primary = faces.OrderByDescending(f => f.Confidence).First();
                var embedding = _faceService.GetEmbedding(file.FullPath, primary);

                var match = _store.Match(embedding);
                if (match is null)
                {
                    // New, unseen face — create a placeholder person
                    _store.CreatePerson(embedding);
                    _store.Save();
                }
                else
                {
                    var (person, confidence) = match.Value;
                    _store.AddEmbedding(embedding, person);

                    if (person.Name is not null || person.PreferredFolder is not null)
                        suggestions.Add(new SuggestionResult(
                            person.Name,
                            person.PreferredFolder,
                            confidence,
                            "face"));
                }

                return (IList<SuggestionResult>)suggestions;
            }
            catch { return []; }
        }, ct);
    }

    // ── Document suggestions (name + topic matching) ──────────────────────────

    private IList<SuggestionResult> GetDocumentSuggestions(FileRecord file)
    {
        var suggestions = new List<SuggestionResult>();
        var content     = file.DocumentContent;

        // Person name match via title, header, or author
        var personMatch = _store.MatchDocumentText(
            content?.Title, content?.HeaderText, content?.Author);

        if (personMatch is not null)
        {
            var (person, confidence) = personMatch.Value;
            if (person.Name is not null || person.PreferredFolder is not null)
                suggestions.Add(new SuggestionResult(
                    person.Name,
                    person.PreferredFolder,
                    confidence,
                    "doc-name"));
        }

        // Topic cluster match via keywords
        var keywords = content?.Keywords ?? [];
        if (keywords.Count > 0)
        {
            var cluster = _store.MatchTopic(keywords);
            if (cluster?.PreferredFolder is not null)
                suggestions.Add(new SuggestionResult(
                    null,
                    cluster.PreferredFolder,
                    0.6f,
                    "doc-topic"));
        }

        return suggestions;
    }

    // ── Learning ──────────────────────────────────────────────────────────────

    public void NotifyFileMoved(FileRecord file, string destFolder, string? newName,
                                IList<SuggestionResult> priorSuggestions)
    {
        // Update person record if a face suggestion was acted on
        var faceSuggestion = priorSuggestions.FirstOrDefault(s => s.Source == "face");
        if (faceSuggestion is not null && file.Category == FileCategory.Image)
        {
            // Find the person whose folder/name matches the suggestion (best proxy for "this person")
            var person = _store.Persons
                .Where(p => p.PreferredFolder == faceSuggestion.SuggestedFolder
                         || p.Name            == faceSuggestion.SuggestedName)
                .FirstOrDefault();
            if (person is not null)
            {
                person.PreferredFolder = destFolder;
                if (newName is not null)
                    person.Name = System.IO.Path.GetFileNameWithoutExtension(newName);
                _store.Save();
            }
        }

        // Learn topic → folder for documents
        if (file.Category == FileCategory.Document)
        {
            var keywords = file.DocumentContent?.Keywords ?? [];
            if (keywords.Count > 0)
                _store.LearnTopicFolder(keywords, destFolder);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _faceService?.Dispose();
        _disposed = true;
    }
}
