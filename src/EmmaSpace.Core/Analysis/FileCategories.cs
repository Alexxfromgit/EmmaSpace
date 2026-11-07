namespace EmmaSpace.Core.Analysis;

public enum FileCategory
{
    Other,
    Video,
    Audio,
    Images,
    Documents,
    Archives,
    Code,
    Applications,
    System,
    Data,
}

public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> s_map = Build();
    private static readonly Dictionary<string, FileCategory>.AlternateLookup<ReadOnlySpan<char>> s_lookup =
        s_map.GetAlternateLookup<ReadOnlySpan<char>>();

    public static FileCategory Categorize(ReadOnlySpan<char> extension) =>
        !extension.IsEmpty && s_lookup.TryGetValue(extension, out var category) ? category : FileCategory.Other;

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory category, string extensions)
        {
            foreach (var ext in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries)) map[ext] = category;
        }

        Add(FileCategory.Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg m2ts mts ts 3gp vob");
        Add(FileCategory.Audio, "mp3 wav flac aac ogg wma m4a opus aiff aif mid midi alac ape");
        Add(FileCategory.Images, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 cr3 nef arw dng svg ico psd ai xcf avif");
        Add(FileCategory.Documents, "pdf doc docx xls xlsx ppt pptx odt ods odp rtf txt md epub mobi pages numbers key csv tex");
        Add(FileCategory.Archives, "zip rar 7z tar gz tgz bz2 xz zst cab iso img dmg wim lz lzma");
        Add(FileCategory.Code, "cs cpp c h hpp cc java js ts tsx jsx py rb go rs swift kt m mm php html css scss json xml yaml yml sql sh ps1 bat cmd sln slnx csproj vcxproj props targets pdb obj o lib a ilk ipch");
        Add(FileCategory.Applications, "exe msi msix appx dll so dylib app apk jar pkg deb rpm sys drv ocx");
        Add(FileCategory.System, "log tmp temp bak old cache etl evtx dmp mdmp hiberfil pagefile swp regtrans-ms blf");
        Add(FileCategory.Data, "db sqlite sqlite3 mdb accdb vhd vhdx vmdk vdi qcow2 pst ost bin dat pak mdf ldf");
        return map;
    }
}
