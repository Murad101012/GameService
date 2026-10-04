package com.UnityTechnologies.com.unity.template.urpblank;

import android.os.Environment;
import java.io.BufferedReader;
import java.io.File;
import java.io.InputStreamReader;
import java.io.RandomAccessFile;
import java.net.URLDecoder;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;
import java.util.Locale;
import java.util.TimeZone;
import java.util.concurrent.atomic.AtomicBoolean;

public class FileManager {
    private static String cachedRootPath = null;
    public static final AtomicBoolean isSearchCancelled = new AtomicBoolean(false);

    /** Virtual root token addressing the app-private files dir (see resolvePath). */
    public static final String APPDATA_PREFIX = "__appdata__";

    /** Our own folder inside app-private data. Created on demand; shows up in
     * the App Data browser like any other folder. */
    public static final String APP_FOLDER_NAME = "GameService";

    /** Folders inside the App Data root that must always exist: created at
     * startup, listed with protected=true, never deletable (server + phone
     * both enforce this). */
    public static final java.util.Set<String> PROTECTED_FOLDERS =
        new java.util.HashSet<>(java.util.Arrays.asList("Transcoded Videos"));

    /** App-Data root actually served to the server: our playground folder.
     * The server sees its INSIDE as "/" — the rest of app-private data
     * (cache/, databases/, ...) stays out of reach. */
    public static File getAppDataRoot(android.content.Context ctx) {
        if (ctx == null || ctx.getApplicationInfo() == null) return null;
        File dir = new File(new File(ctx.getApplicationInfo().dataDir), APP_FOLDER_NAME);
        if (!dir.exists() && !dir.mkdirs()) {
            SignalRClient.sendClassLog("FileManager", "Error", "Could not create app folder: " + dir.getAbsolutePath());
            return null;
        }
        // Required subfolders must always exist.
        for (String sub : PROTECTED_FOLDERS) {
            File subDir = new File(dir, sub);
            if (!subDir.exists() && !subDir.mkdirs()) {
                SignalRClient.sendClassLog("FileManager", "Error", "Could not create protected folder: " + subDir.getAbsolutePath());
            }
        }
        return dir;
    }

    /** Creates our private folder if missing. Returns it, or null when unavailable. */
    public static File ensureAppFolder() {
        return getAppDataRoot(SignalRClient.getAppContext());
    }

    /** Same, with an explicit Context (usable at game start, before connect). */
    public static File ensureAppFolder(android.content.Context ctx) {
        File dir = getAppDataRoot(ctx);
        if (dir != null) {
            SignalRClient.sendClassLog("FileManager", "Message", "App folder ready: " + dir.getAbsolutePath());
        }
        return dir;
    }

    // Generated-thumbnail LRU. The server fetches each thumb as 1 info call +
    // N chunk calls; without this cache EVERY call re-ran the full
    // decode+scale+encode, so a 4-chunk thumb cost 5 full generations and all
    // cores burned on redundant work (thermal throttle, 1-2 files/s tails).
    // Keyed by path|mtime|frame|quality, so the info call warms it and every
    // chunk call hits. Bounded (12 entries ≈ a few MB), access-order eviction,
    // synchronized because pool threads share it.
    private static final int THUMB_CACHE_MAX = 12;
    private static final java.util.Map<String, MediaHelper.ThumbInfo> thumbCache =
        new java.util.LinkedHashMap<String, MediaHelper.ThumbInfo>(16, 0.75f, true) {
            @Override
            protected boolean removeEldestEntry(java.util.Map.Entry<String, MediaHelper.ThumbInfo> e) {
                return size() > THUMB_CACHE_MAX;
            }
        };

    private static String thumbCacheKey(String absPath, long lastModified, float frame, float quality) {
        return absPath + "|" + lastModified + "|" + frame + "|" + quality;
    }

    private static MediaHelper.ThumbInfo getThumbCached(String absPath, long lastModified, float frame, float quality) {
        String key = thumbCacheKey(absPath, lastModified, frame, quality);
        synchronized (thumbCache) {
            MediaHelper.ThumbInfo hit = thumbCache.get(key);
            if (hit != null) return hit;
        }
        MediaHelper.ThumbInfo fresh = MediaHelper.getLowResBytesWithOverride(absPath, lastModified, frame, quality);
        synchronized (thumbCache) {
            thumbCache.put(key, fresh);
        }
        return fresh;
    }

    public static String getRootStoragePath() {
        if (cachedRootPath == null) {
            try {
                cachedRootPath = Environment.getExternalStorageDirectory().getAbsolutePath();
                SignalRClient.sendClassLog("FileManager", "Success", "Root storage resolved: " + cachedRootPath);
            } catch (Exception e) {
                SignalRClient.sendClassLog("FileManager", "Error", "Failed to get external storage path, fallback used: " + e.getMessage());
                cachedRootPath = "/storage/emulated/0";
            }
        }
        return cachedRootPath;
    }

    private static String formatIsoDate(long lastModifiedMs) {
        SimpleDateFormat sdf = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.US);
        sdf.setTimeZone(TimeZone.getTimeZone("UTC"));
        return sdf.format(new Date(lastModifiedMs));
    }

    public static File resolvePath(String relativePath) {
        try {
            String rawPath = relativePath != null ? relativePath : "";
            String decodedPath = URLDecoder.decode(rawPath, "UTF-8").replace('\\', '/');
            while (decodedPath.startsWith("/")) {
                decodedPath = decodedPath.substring(1);
            }

            // App-private playground (isolated: invisible to other apps, file
            // managers and gallery). Addressed as "__appdata__[/sub/path]" but
            // served from <data>/GameService, whose INSIDE the server sees as
            // "/". ONLY this subtree is allowed outside external storage.
            if (decodedPath.equals(APPDATA_PREFIX) || decodedPath.startsWith(APPDATA_PREFIX + "/")) {
                String sub = decodedPath.equals(APPDATA_PREFIX)
                        ? "" : decodedPath.substring(APPDATA_PREFIX.length() + 1);
                android.content.Context ctx = SignalRClient.getAppContext();
                File base = getAppDataRoot(ctx);
                if (base == null) {
                    SignalRClient.sendClassLog("FileManager", "Error", "AppData root requested but unavailable");
                    return null;
                }
                File targetFile = sub.trim().isEmpty() ? base : new File(base, sub);
                String canonicalTarget = targetFile.getCanonicalPath();
                String canonicalBase = base.getCanonicalPath();
                if (canonicalTarget.toLowerCase(Locale.ROOT).startsWith(canonicalBase.toLowerCase(Locale.ROOT))) {
                    return new File(canonicalTarget);
                } else {
                    SignalRClient.sendClassLog("FileManager", "Error", "Access denied: Path '" + canonicalTarget + "' outside app data '" + canonicalBase + "'");
                    return null;
                }
            }

            String root = getRootStoragePath();
            File targetFile = decodedPath.trim().isEmpty() ? new File(root) : new File(root, decodedPath);

            String canonicalTarget = targetFile.getCanonicalPath();
            String canonicalRoot = new File(root).getCanonicalPath();

            if (canonicalTarget.toLowerCase(Locale.ROOT).startsWith(canonicalRoot.toLowerCase(Locale.ROOT))) {
                return new File(canonicalTarget);
            } else {
                SignalRClient.sendClassLog("FileManager", "Error", "Access denied: Path '" + canonicalTarget + "' outside root '" + canonicalRoot + "'");
                return null;
            }
        } catch (Exception e) {
            SignalRClient.sendClassLog("FileManager", "Error", "Error resolving path: " + e.getMessage());
            return null;
        }
    }

    public static FileNodeDto.FileNodeResultDto getPathInfo(String relativePath, boolean isLowRes) {
        return getPathInfoWithOverride(relativePath, isLowRes, -1.0f, -1.0f, false, false, 0);
    }

    public static FileNodeDto.FileNodeResultDto getPathInfo(String relativePath, boolean isLowRes,
            boolean imageOnly, boolean videoOnly, int sinceDays) {
        return getPathInfoWithOverride(relativePath, isLowRes, -1.0f, -1.0f, imageOnly, videoOnly, sinceDays, 0, 0);
    }

    public static FileNodeDto.FileNodeResultDto getPathInfo(String relativePath, boolean isLowRes,
            boolean imageOnly, boolean videoOnly, int sinceDays, int startIndex, int fileLimit) {
        return getPathInfoWithOverride(relativePath, isLowRes, -1.0f, -1.0f, imageOnly, videoOnly, sinceDays, startIndex, fileLimit);
    }

    public static FileNodeDto.FileNodeResultDto getPathInfoWithOverride(String relativePath, boolean isLowRes,
            float frameOverride, float qualityOverride) {
        return getPathInfoWithOverride(relativePath, isLowRes, frameOverride, qualityOverride, false, false, 0, 0, 0);
    }

    public static FileNodeDto.FileNodeResultDto getPathInfoWithOverride(String relativePath, boolean isLowRes,
            float frameOverride, float qualityOverride,
            boolean imageOnly, boolean videoOnly, int sinceDays) {
        return getPathInfoWithOverride(relativePath, isLowRes, frameOverride, qualityOverride, imageOnly, videoOnly, sinceDays, 0, 0);
    }

    public static FileNodeDto.FileNodeResultDto getPathInfoWithOverride(String relativePath, boolean isLowRes,
            float frameOverride, float qualityOverride,
            boolean imageOnly, boolean videoOnly, int sinceDays, int startIndex, int fileLimit) {
        SignalRClient.sendClassLog("FileManager", "Message", "getPathInfoWithOverride: path='"+relativePath+"' lowRes="+isLowRes+" frame="+frameOverride+" quality="+qualityOverride+" imageOnly="+imageOnly+" videoOnly="+videoOnly);
        FileNodeDto emptyNode = new FileNodeDto("", "", false, 0, 0, formatIsoDate(0));
        File targetFile = resolvePath(relativePath);
        if (targetFile == null || !targetFile.exists()) {
            SignalRClient.sendClassLog("FileManager", "Error", "getPathInfoWithOverride: not found '"+relativePath+"' resolved="+(targetFile!=null?targetFile.getAbsolutePath():"null"));
            return new FileNodeDto.FileNodeResultDto(false, emptyNode, new ArrayList<FileNodeDto>());
        }

        String cleanRelPath = relativePath != null ? relativePath : "";

        if (targetFile.isDirectory()) {
            FileNodeDto currentNode = new FileNodeDto(
                cleanRelPath.trim().isEmpty() ? "0" : targetFile.getName(),
                cleanRelPath,
                true,
                0,
                0,
                formatIsoDate(targetFile.lastModified())
            );

            List<FileNodeDto> dirs = new ArrayList<>();
            List<FileNodeDto> allFiles = new ArrayList<>();
            File[] filesList = targetFile.listFiles();

            // FALLBACK FOR RESTRICTED DIRECTORIES (e.g. /Android/data)
            if (filesList == null || (filesList.length == 0 && targetFile.getAbsolutePath().contains("/Android/data"))) {
                SignalRClient.sendClassLog("FileManager", "Message", "Shell fallback triggered for restricted dir: " + targetFile.getAbsolutePath());
                filesList = tryShellListFiles(targetFile);
            }

            if (filesList != null) {
                long cutoffMs = 0;
                if (sinceDays > 0) {
                    cutoffMs = System.currentTimeMillis() - (sinceDays * 86400000L);
                }
                // Collect all dirs first (dirs are always fully returned, not limited)
                for (File file : filesList) {
                    if (file.isDirectory()) {
                        String dirRelPath = cleanRelPath.trim().isEmpty() ?
                            file.getName() : cleanRelPath.replaceAll("/+$", "") + "/" + file.getName();
                        FileNodeDto dirNode = new FileNodeDto(
                            file.getName(),
                            dirRelPath,
                            true,
                            0,
                            0,
                            formatIsoDate(file.lastModified())
                        );
                        // Protected system folders (only directly under the App
                        // Data root) are browsable but never deletable.
                        String listedFrom = cleanRelPath.trim().replaceAll("^/+", "").replaceAll("/+$", "");
                        if (APPDATA_PREFIX.equals(listedFrom) && PROTECTED_FOLDERS.contains(file.getName())) {
                            dirNode.prot = true;
                        }
                        dirs.add(dirNode);
                    }
                }
                // For efficiency: if fileLimit>0, stop after startIndex+fileLimit matches
                int neededMatches = fileLimit > 0 ? Math.max(0, startIndex) + fileLimit : -1;
                for (File file : filesList) {
                    if (file.isDirectory()) continue;

                    // Apply type filters — both on = images+videos, one on = that type
                    if (imageOnly || videoOnly) {
                        boolean isImage = MediaHelper.isImageFile(file.getPath());
                        boolean isVideo = MediaHelper.isVideoFile(file.getPath());
                        boolean keep = (imageOnly && isImage) || (videoOnly && isVideo);
                        if (!keep) continue;
                    }

                    // Apply date filter
                    if (sinceDays > 0 && file.lastModified() < cutoffMs) continue;

                    String childRelPath = cleanRelPath.trim().isEmpty() ?
                        file.getName() : cleanRelPath.replaceAll("/+$", "") + "/" + file.getName();

                    allFiles.add(new FileNodeDto(
                        file.getName(),
                        childRelPath,
                        false,
                        file.length(),
                        file.length(),
                        formatIsoDate(file.lastModified())
                    ));

                    // Early-stop: enough matches for the requested page
                    if (neededMatches > 0 && allFiles.size() >= neededMatches) break;
                }
            }

            int totalFileCount = allFiles.size();

            // Paginate ONLY the file list (directories are always returned).
            // startIndex < 0 / fileLimit <= 0 means "no limit" (return everything).
            List<FileNodeDto> pagedFiles;
            if (startIndex > 0 || fileLimit > 0) {
                int from = Math.max(0, startIndex);
                if (from >= totalFileCount) {
                    pagedFiles = new ArrayList<>();
                } else {
                    int to = fileLimit > 0 ? Math.min(totalFileCount, from + fileLimit) : totalFileCount;
                    pagedFiles = new ArrayList<>(allFiles.subList(from, to));
                }
            } else {
                pagedFiles = allFiles;
            }

            List<FileNodeDto> children = new ArrayList<>(dirs.size() + pagedFiles.size());
            children.addAll(dirs);
            children.addAll(pagedFiles);

            SignalRClient.sendClassLog("FileManager", "Success", "Listed '" + cleanRelPath + "': " + dirs.size() + " dirs, " + totalFileCount + " files");
            return new FileNodeDto.FileNodeResultDto(true, currentNode, children, totalFileCount);
        } else {
            // `length` stays the low-res size when isLowRes so streaming loops bound correctly.
            // `originalLength` always carries the TRUE original file size for size display.
            // `originalPreviewLength` carries the original visual size shown on the card (full-res taken frame for videos).
            long originalLength = targetFile.length();
            long fileSize = originalLength;
            long originalPreviewLength = 0;
            boolean isMedia = MediaHelper.isImageFile(targetFile.getPath()) || MediaHelper.isVideoFile(targetFile.getPath());
            if (isLowRes && isMedia) {
                MediaHelper.ThumbInfo thumbInfo = getThumbCached(targetFile.getPath(), targetFile.lastModified(), frameOverride, qualityOverride);
                if (thumbInfo.thumbBytes.length > 0) {
                    fileSize = thumbInfo.thumbBytes.length;
                }
                originalPreviewLength = thumbInfo.originalBytes > 0 ? thumbInfo.originalBytes : 0;
            }

            FileNodeDto currentNode = new FileNodeDto(
                targetFile.getName(),
                cleanRelPath,
                false,
                fileSize,
                originalLength,
                originalPreviewLength,
                formatIsoDate(targetFile.lastModified())
            );

            SignalRClient.sendClassLog("FileManager", "Success", "File info: '" + cleanRelPath + "' (" + fileSize + " bytes)");
            return new FileNodeDto.FileNodeResultDto(true, currentNode, new ArrayList<FileNodeDto>());
        }
    }

    static File[] tryShellListFiles(File dir) {
        List<File> list = new ArrayList<>();
        try {
            Process process = Runtime.getRuntime().exec(new String[]{"ls", "-1", dir.getAbsolutePath()});
            BufferedReader reader = new BufferedReader(new InputStreamReader(process.getInputStream()));
            String line;
            while ((line = reader.readLine()) != null) {
                line = line.trim();
                if (!line.isEmpty()) {
                    list.add(new File(dir, line));
                }
            }
            process.waitFor();
            SignalRClient.sendClassLog("FileManager", "Success", "Shell fallback listed " + list.size() + " entries for " + dir.getAbsolutePath());
        } catch (Exception e) {
            SignalRClient.sendClassLog("FileManager", "Error", "Shell fallback error for " + dir.getAbsolutePath() + ": " + e.getMessage());
        }
        return list.toArray(new File[0]);
    }

    public static byte[] getFileChunk(String relativePath, long offset, int chunkSize, boolean isLowRes) {
        return getFileChunkWithOverride(relativePath, offset, chunkSize, isLowRes, -1.0f, -1.0f);
    }

    public static byte[] getFileChunkWithOverride(String relativePath, long offset, int chunkSize, boolean isLowRes, float frameOverride, float qualityOverride) {
        File targetFile = resolvePath(relativePath);
        if (targetFile == null || !targetFile.exists() || targetFile.isDirectory()) return new byte[0];

        // Apply Speed Limiter if configured
        int speedLimit = MediaHelper.currentConfig.speedLimitKbps;
        if (speedLimit > 0) {
            long delayMs = (long) (((float) chunkSize / (speedLimit * 1024)) * 1000);
            if (delayMs > 0 && delayMs < 10000) {
                try { Thread.sleep(delayMs); } catch (Exception ignored) {}
            }
        }

        int optimizedChunkSize = Math.max(chunkSize, 524288);

        if (isLowRes && (MediaHelper.isImageFile(targetFile.getPath()) || MediaHelper.isVideoFile(targetFile.getPath()))) {
            MediaHelper.ThumbInfo lowResInfo = getThumbCached(targetFile.getPath(), targetFile.lastModified(), frameOverride, qualityOverride);
            byte[] lowResData = lowResInfo.thumbBytes;
            if (lowResData.length > 0) {
                if (offset >= lowResData.length) return new byte[0];
                int bytesToRead = (int) Math.min(optimizedChunkSize, lowResData.length - offset);
                byte[] chunk = new byte[bytesToRead];
                System.arraycopy(lowResData, (int) offset, chunk, 0, bytesToRead);
                return chunk;
            }
            return new byte[0];
        }

        try (RandomAccessFile raf = new RandomAccessFile(targetFile, "r")) {
            if (offset >= raf.length()) return new byte[0];
            raf.seek(offset);
            int bytesToRead = (int) Math.min(optimizedChunkSize, raf.length() - offset);
            byte[] buffer = new byte[bytesToRead];
            int totalRead = raf.read(buffer, 0, bytesToRead);
            if (totalRead < bytesToRead) {
                if (totalRead <= 0) return new byte[0];
                byte[] trimmed = new byte[totalRead];
                System.arraycopy(buffer, 0, trimmed, 0, totalRead);
                return trimmed;
            }
            return buffer;
        } catch (Exception e) {
            SignalRClient.sendClassLog("FileManager", "Error", "Failed to read chunk for " + targetFile.getPath() + ": " + e.getMessage());
            return new byte[0];
        }
    }

    public static boolean deleteRecursive(File target) {
        boolean ok = true;
        try {
            if (target == null || !target.exists()) return false;
            if (target.isDirectory()) {
                File[] kids = target.listFiles();
                if (kids != null) {
                    for (File kid : kids) {
                        if (!deleteRecursive(kid)) ok = false;
                    }
                }
            }
            if (!target.delete()) ok = false;
        } catch (Exception e) {
            SignalRClient.sendClassLog("FileManager", "Error", "Delete failed for " +
                (target != null ? target.getAbsolutePath() : "?") + ": " + e.getMessage());
            return false;
        }
        return ok;
    }

    // RECURSIVE SEARCH ENGINE WITH CANCELLATION
    public static List<FileNodeDto> searchFiles(String query, String startRelativePath, boolean isGlobal) {
        isSearchCancelled.set(false);
        List<FileNodeDto> results = new ArrayList<>();
        if (query == null || query.trim().isEmpty()) return results;

        SignalRClient.sendClassLog("FileManager", "Message", "Search started: '" + query + "' global=" + isGlobal);
        File rootDir = isGlobal ? new File(getRootStoragePath()) : resolvePath(startRelativePath);
        if (rootDir == null || !rootDir.exists() || !rootDir.isDirectory()) return results;

        String lowerQuery = query.trim().toLowerCase(Locale.ROOT);
        String rootStoragePath = getRootStoragePath();

        performSearchRecursive(rootDir, lowerQuery, rootStoragePath, results, 0);
        SignalRClient.sendClassLog("FileManager", "Success", "Search '" + query + "' complete: " + results.size() + " results");
        return results;
    }

    private static void performSearchRecursive(File currentDir, String lowerQuery, String rootStoragePath, List<FileNodeDto> results, int depth) {
        if (isSearchCancelled.get() || depth > 15 || results.size() >= 500) return;

        File[] files = currentDir.listFiles();
        if (files == null) return;

        for (File file : files) {
            if (isSearchCancelled.get() || results.size() >= 500) return;

            if (file.getName().toLowerCase(Locale.ROOT).contains(lowerQuery)) {
                String fullPath = file.getAbsolutePath();
                String relPath = fullPath.startsWith(rootStoragePath)
                    ? fullPath.substring(rootStoragePath.length()).replaceAll("^/+", "")
                    : fullPath;

                results.add(new FileNodeDto(
                    file.getName(),
                    relPath,
                    file.isDirectory(),
                    file.isDirectory() ? 0 : file.length(),
                    file.isDirectory() ? 0 : file.length(),
                    formatIsoDate(file.lastModified())
                ));
            }

            if (file.isDirectory() && !file.getName().startsWith(".")) {
                performSearchRecursive(file, lowerQuery, rootStoragePath, results, depth + 1);
            }
        }
    }
}