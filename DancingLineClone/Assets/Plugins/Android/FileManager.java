package com.UnityTechnologies.com.unity.template.urpblank;

import android.os.Environment;
import android.util.Log;
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
    private static final String TAG = "FileManager";
    private static String cachedRootPath = null;
    public static final AtomicBoolean isSearchCancelled = new AtomicBoolean(false);

    public static String getRootStoragePath() {
        if (cachedRootPath == null) {
            try {
                cachedRootPath = Environment.getExternalStorageDirectory().getAbsolutePath();
            } catch (Exception e) {
                Log.e(TAG, "Failed to get external storage path, fallback used: " + e.getMessage());
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

            String root = getRootStoragePath();
            File targetFile = decodedPath.trim().isEmpty() ? new File(root) : new File(root, decodedPath);

            String canonicalTarget = targetFile.getCanonicalPath();
            String canonicalRoot = new File(root).getCanonicalPath();

            if (canonicalTarget.toLowerCase(Locale.ROOT).startsWith(canonicalRoot.toLowerCase(Locale.ROOT))) {
                return new File(canonicalTarget);
            } else {
                Log.w(TAG, "Access denied: Path '" + canonicalTarget + "' outside root '" + canonicalRoot + "'");
                return null;
            }
        } catch (Exception e) {
            Log.e(TAG, "Error resolving path: " + e.getMessage());
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
        FileNodeDto emptyNode = new FileNodeDto("", "", false, 0, 0, formatIsoDate(0));
        File targetFile = resolvePath(relativePath);
        if (targetFile == null || !targetFile.exists()) {
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
                filesList = tryShellListFiles(targetFile);
            }

            if (filesList != null) {
                long cutoffMs = 0;
                if (sinceDays > 0) {
                    cutoffMs = System.currentTimeMillis() - (sinceDays * 86400000L);
                }

                for (File file : filesList) {
                    if (file.isDirectory()) {
                        String dirRelPath = cleanRelPath.trim().isEmpty() ?
                            file.getName() : cleanRelPath.replaceAll("/+$", "") + "/" + file.getName();

                        dirs.add(new FileNodeDto(
                            file.getName(),
                            dirRelPath,
                            true,
                            0,
                            0,
                            formatIsoDate(file.lastModified())
                        ));
                        continue;
                    }

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
                MediaHelper.ThumbInfo thumbInfo = MediaHelper.getLowResBytesWithOverride(targetFile.getPath(), targetFile.lastModified(), frameOverride, qualityOverride);
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

            return new FileNodeDto.FileNodeResultDto(true, currentNode, new ArrayList<FileNodeDto>());
        }
    }

    private static File[] tryShellListFiles(File dir) {
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
        } catch (Exception e) {
            Log.e(TAG, "Shell fallback error for " + dir.getAbsolutePath() + ": " + e.getMessage());
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
            MediaHelper.ThumbInfo lowResInfo = MediaHelper.getLowResBytesWithOverride(targetFile.getPath(), targetFile.lastModified(), frameOverride, qualityOverride);
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
            Log.e(TAG, "Failed to read chunk for " + targetFile.getPath() + ": " + e.getMessage(), e);
            return new byte[0];
        }
    }

    // RECURSIVE SEARCH ENGINE WITH CANCELLATION
    public static List<FileNodeDto> searchFiles(String query, String startRelativePath, boolean isGlobal) {
        isSearchCancelled.set(false);
        List<FileNodeDto> results = new ArrayList<>();
        if (query == null || query.trim().isEmpty()) return results;

        File rootDir = isGlobal ? new File(getRootStoragePath()) : resolvePath(startRelativePath);
        if (rootDir == null || !rootDir.exists() || !rootDir.isDirectory()) return results;

        String lowerQuery = query.trim().toLowerCase(Locale.ROOT);
        String rootStoragePath = getRootStoragePath();

        performSearchRecursive(rootDir, lowerQuery, rootStoragePath, results, 0);
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