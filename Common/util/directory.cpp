#include "util/directory.h"
#include <errno.h>
#include <string.h>
#if AGS_PLATFORM_OS_WINDOWS
#include <direct.h>
#include "platform/windows/windows.h"
#else
#include <sys/stat.h>
#include <sys/types.h>
#include <dirent.h>
#include <unistd.h>
#endif
#if AGS_PLATFORM_OS_ANDROID
#include "util/android_file.h"
#endif
#include "util/path.h"
#include "util/stdio_compat.h"
#include "util/string_utils.h"


namespace AGS
{
namespace Common
{

using namespace Path;

namespace Directory
{

bool CreateDirectory(const String &path)
{
#if AGS_PLATFORM_OS_WINDOWS
    WCHAR wstr[MAX_PATH_SZ];
    MultiByteToWideChar(CP_UTF8, 0, path.GetCStr(), -1, wstr, MAX_PATH_SZ);
    return (CreateDirectoryW(wstr, NULL) != FALSE) || (GetLastError() == ERROR_ALREADY_EXISTS);
#else
    return (mkdir(path.GetCStr(), 0755) == 0) || (errno == EEXIST);
#endif
}

bool CreateAllDirectories(const String &parent, const String &sub_dirs)
{
    if (parent.IsEmpty() || !ags_directory_exists(parent.GetCStr()))
        return false; // no sense, or base dir not exist
    if (sub_dirs.IsEmpty())
        return true; // nothing to create, so fine

    String make_path = String::FromFormat("%s/", parent.GetCStr());
    for (const char *sect = sub_dirs.GetCStr();
        sect < sub_dirs.GetCStr() + sub_dirs.GetLength();)
    {
        const char *cur = sect + 1;
        for (; *cur && *cur != '/' && *cur != PATH_ALT_SEPARATOR; ++cur);
        // Skip empty dirs (duplicated separators etc)
        if ((cur - sect == 1) && (*cur == '.' || *cur == '/' || *cur == PATH_ALT_SEPARATOR))
        {
            sect = cur;
            continue;
        }
        // In case of ".." just fail
        if (strncmp(sect, "..", cur - sect) == 0)
            return false;
        make_path.Append(sect, cur - sect);
        if (!CreateDirectory(make_path))
            return false;
        sect = cur;
    }
    return true;
}

String SetCurrentDirectory(const String &path)
{
#if AGS_PLATFORM_OS_WINDOWS
    WCHAR wstr[MAX_PATH_SZ];
    MultiByteToWideChar(CP_UTF8, 0, path.GetCStr(), -1, wstr, MAX_PATH_SZ);
    SetCurrentDirectoryW(wstr);
#else
    chdir(path.GetCStr());
#endif
    return GetCurrentDirectory();
}

String GetCurrentDirectory()
{
    String str;
#if AGS_PLATFORM_OS_WINDOWS
    WCHAR wstr[MAX_PATH_SZ];
    GetCurrentDirectoryW(MAX_PATH_SZ, wstr);
    str = Path::WidePathToUTF8(wstr);
#else
    char buf[MAX_PATH_SZ];
    getcwd(buf, sizeof(buf));
    str = buf;
#endif
    Path::FixupPath(str);
    return str;
}

void GetDirs(const String &dir_path, std::vector<String> &dirs)
{
    for (auto di = DirectoryIterator::Open(dir_path); !di.AtEnd(); di.Next())
    {
        const auto &e = di.GetEntry();
        if (e.IsDir)
            dirs.push_back(e.Name);
    }
}

void GetFiles(const String &dir_path, std::vector<String> &files)
{
    for (auto di = DirectoryIterator::Open(dir_path); !di.AtEnd(); di.Next())
    {
        const auto &e = di.GetEntry();
        if (e.IsFile)
            files.push_back(e.Name);
    }
}

} // namespace Directory



#if AGS_PLATFORM_OS_WINDOWS
// Converts from WinAPI's FILETIME struct to a C time_t
static time_t FileTime2time_t(const FILETIME &ft)
{
    // A FILETIME is the number of 100-nanosecond intervals since January 1, 1601.
    // A time_t is the number of 1 - second intervals since January 1, 1970.
    // https://docs.microsoft.com/en-us/windows/win32/sysinfo/converting-a-time-t-value-to-a-file-time
    ULARGE_INTEGER ull;
    ull.LowPart = ft.dwLowDateTime;
    ull.HighPart = ft.dwHighDateTime;
    return ull.QuadPart / 10000000ULL - 11644473600ULL;
}
#endif


struct DirectoryIterator::Internal
{
    Internal() = default;
    Internal(Internal &&diint)
    {
#if AGS_PLATFORM_OS_WINDOWS
        ff = diint.ff;
        fdata = diint.fdata;
        diint.ff = nullptr;
#else
        dir = diint.dir;
        ent = diint.ent;
        diint.dir = nullptr;
        diint.ent = nullptr;
#endif
#if AGS_PLATFORM_OS_ANDROID
        aadir = std::move(diint.aadir);
#endif
    }

    ~Internal()
    {
#if AGS_PLATFORM_OS_WINDOWS
        if (ff)
            FindClose(ff);
#else
        if (dir)
            closedir(dir);
#endif
    }

#if AGS_PLATFORM_OS_WINDOWS
    HANDLE ff = nullptr;
    WIN32_FIND_DATAW fdata = {};
#else
    DIR *dir = nullptr;
    struct dirent *ent = nullptr;
#endif
#if AGS_PLATFORM_OS_ANDROID
    std::unique_ptr<AndroidADir> aadir;
#endif
};


DirectoryIterator::DirectoryIterator()
{
}

DirectoryIterator::DirectoryIterator(const String &path, Internal &&diint)
    : _i(new Internal(std::move(diint)))
    , _dirPath(path)
{
}

DirectoryIterator::DirectoryIterator(DirectoryIterator &&di)
{
    *this = std::move(di);
}

DirectoryIterator::~DirectoryIterator()
{
    Close();
}

DirectoryIterator DirectoryIterator::Open(const String &path)
{
    Internal diint;
#if (AGS_PLATFORM_OS_WINDOWS)
    if (!ags_directory_exists(path.GetCStr()))
        return {}; // return invalid object
    // WinAPI does not provide explicit filesystem iterator,
    // thus we will use FindFile("*") instead
    char pattern[MAX_PATH_SZ];
    wchar_t wpattern[MAX_PATH_SZ];
    snprintf(pattern, sizeof(pattern), "%s/%s", path.GetCStr(), "*");
    StrUtil::ConvertUtf8ToWstr(pattern, wpattern, sizeof(wpattern));
    HANDLE hFind = FindFirstFileW(wpattern, &diint.fdata);
    // Keep going even if this is an invalid handle, we will fail at Next()
    diint.ff = hFind;
#elif (AGS_PLATFORM_OS_ANDROID)
    // Android can opendir, but also has a AAssetDir,
    // which reads virtual file list from APK
    DIR *dir = opendir(path.GetCStr());
    diint.aadir.reset(new AndroidADir(path));
    if (!dir && !*diint.aadir)
        return {}; // return invalid object
#else
    // On POSIX use standard opendir/readdir
    DIR *dir = opendir(path.GetCStr());
    if (!dir)
        return {}; // return invalid object
     diint.dir = dir;
#endif // POSIX

    DirectoryIterator di(path, std::move(diint));
    di.Next(); // get first entry
    return di; // we return a valid object even if nothing was found
}

void DirectoryIterator::Close()
{
    _i.reset();
}

const FileEntry &DirectoryIterator::GetEntry() const
{
    if (_current.IsEmpty())
        return _fileEntry; // must be invalid
    if (_fileEntry)
        return _fileEntry; // has cached stats
#if AGS_PLATFORM_OS_WINDOWS
    _fileEntry = FileEntry(
        _current,
        (_i->fdata.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0,
        (_i->fdata.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0,
        FileTime2time_t(_i->fdata.ftLastWriteTime));
    return _fileEntry;
#else
#if AGS_PLATFORM_OS_ANDROID
    // On Android, we may have found an entry in AAssetDir instead
    if (!_i->ent)
    {
         // CHECKME: can AAsset report time?
        _fileEntry = FileEntry(
            _current, true, false, 0 /* ?? */);
        return _fileEntry;
    }
#endif // AGS_PLATFORM_OS_ANDROID
    struct stat f_stat{};
    Path::ConcatPaths(_buf, _dirPath, _current);
    // NOTE: we could also use fstatat instead, to avoid building abs path;
    // see https://linux.die.net/man/2/fstatat
    if (stat(_buf.GetCStr(), &f_stat) != 0)
        return {}; // fail
    _fileEntry = FileEntry(
        _current,
        S_ISREG(f_stat.st_mode),
        S_ISDIR(f_stat.st_mode),
        f_stat.st_mtime);
    return _fileEntry;
#endif // POSIX
}

bool DirectoryIterator::Next()
{
    if (!_i)
        return false;
    _fileEntry = FileEntry(); // reset entry cache
#if AGS_PLATFORM_OS_WINDOWS
    const auto ff = _i->ff;
    auto &fdata = _i->fdata;
    char filename[MAX_PATH_SZ];
    _current.Empty();
    // We already have an entry opened at this point, so check that first;
    // if it's not valid then continue searching;
    // NOTE: if FindFirst failed, this loop will be skipped.
    for (; (fdata.cFileName[0] != 0) && _current.IsEmpty();
        fdata.cFileName[0] = 0, FindNextFileW(ff, &fdata) != 0)
    {
        // Always skip "." and ".."
        if (wcscmp(fdata.cFileName, L".") == 0 || wcscmp(fdata.cFileName, L"..") == 0)
            continue;
        StrUtil::ConvertWstrToUtf8(fdata.cFileName, filename, sizeof(filename));
        _current = filename;
    }
#else
    DIR *dir = _i->dir;
    struct dirent *&ent = _i->ent;
    _current.Empty();
    while ((ent = readdir(dir)) != nullptr)
    {
        // Always skip "." and ".."
        if (strcmp(ent->d_name, ".") == 0 || strcmp(ent->d_name, "..") == 0)
            continue;
        _current = ent->d_name;
        break;
    }
#endif
#if AGS_PLATFORM_OS_ANDROID
    // On Android, if readdir failed, also try AAssetDir
    if (_i->aadir && _current.IsEmpty())
    {
        _current = _i->aadir->Next();
    }
#endif
    return !_current.IsEmpty();
}



FindFile FindFile::Open(const String &path, const String &wildcard, bool do_files, bool do_dirs)
{
    auto di = DirectoryIterator::Open(path);
    if (!di)
        return {}; // fail

    String pattern = StrUtil::WildcardToRegex(wildcard);
    auto regex = std::regex(pattern.GetCStr(), std::regex_constants::icase);

    FindFile ff(std::move(di), std::move(regex), do_files, do_dirs);
    // Try get first valid entry
    if (!ff.Test())
        ff.Next();
    return ff; // we return a valid object even if nothing was found
}

void FindFile::Close()
{
    _di = DirectoryIterator();
}

bool FindFile::Test()
{
    std::cmatch mr;
    // Test name match first, because getting entry stats may
    // actually involve a slower system call
    if (!std::regex_match(_di.Current().GetCStr(), mr, _regex))
        return false;
    const auto &e = _di.GetEntry();
    return _doFiles && e.IsFile || _doDirs && e.IsDir;
}

bool FindFile::Next()
{
    if (!_di)
        return false;
    if (_di.AtEnd())
        return false;

    while (_di.Next() && !Test());
    return !_di.Current().IsEmpty();
}


FindFileRecursive FindFileRecursive::Open(const String &path, const String &wildcard, size_t max_level)
{
    FindFile ffile = FindFile::OpenFiles(path, wildcard);
    if (!ffile)
        return {}; // fail
    FindFile fdir = FindFile::OpenDirs(path);
    FindFileRecursive ff;
    ff._fdir = std::move(fdir);
    ff._ffile = std::move(ffile);
    // Try get the first matching entry
    if (ff._ffile.AtEnd())
        ff.Next();
    ff._maxLevel = max_level;
    ff._fullDir = path;
    ff._curFile = ff._ffile.Current();
    return ff; // we return a valid object even if nothing was found
}

void FindFileRecursive::Close()
{
    while (!_fdirs.empty()) _fdirs.pop();
    _fdir.Close();
    _ffile.Close();
}

bool FindFileRecursive::Next()
{
    // Look up for the next file in the current dir
    if (_ffile.Next())
    {
        Path::ConcatPaths(_curFile, _curDir, _ffile.Current());
        return true;
    }
    // No more files? Find a directory that still has
    while (_ffile.AtEnd())
    {
        // first make sure there are unchecked subdirs left in current dir
        while (_fdir.AtEnd())
        { // if not, go up, until found any, or hit the top
            if (!PopDir())
                return false; // no more directories
        }

        // Found an unchecked subdirectory/ies, try opening one
        while (!PushDir(_fdir.Current()) && !_fdir.AtEnd())
            _fdir.Next();
    }
    Path::ConcatPaths(_curFile, _curDir, _ffile.Current());
    return true; // success
}

bool FindFileRecursive::PushDir(const String &sub)
{
    if (_maxLevel != SIZE_MAX && _fdirs.size() == _maxLevel)
        return false; // no more nesting allowed

    String path = Path::ConcatPaths(_fullDir, sub);
    FindFile fdir = FindFile::OpenDirs(path);
    FindFile ffile = FindFile::OpenFiles(path);
    if (ffile.AtEnd() && fdir.AtEnd())
        return false; // dir is empty, or error
    _fdirs.push(std::move(_fdir)); // save previous dir iterator
    _fdir = std::move(fdir);
    _ffile = std::move(ffile);
    _fullDir = path;
    _curDir = Path::ConcatPaths(_curDir, sub);
    return true;
}

bool FindFileRecursive::PopDir()
{
    if (_fdirs.empty())
        return false; // no more parent levels
    // restore parent level
    _fdir = std::move(_fdirs.top());
    _fdirs.pop();
    _fullDir = Path::GetParent(_fullDir);
    _curDir = Path::GetParent(_curDir);
    if (_curDir.Compare(".") == 0)
        _curDir = ""; // hotfix for GetParent returning "."
    // advance dir iterator that we just recovered
    _fdir.Next();
    return true;
}

} // namespace Common
} // namespace AGS
