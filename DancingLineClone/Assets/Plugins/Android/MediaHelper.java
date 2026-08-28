package com.UnityTechnologies.com.unity.template.urpblank;

import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.media.MediaMetadataRetriever;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.util.Arrays;
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;

public class MediaHelper {
    private static final int BASE_THUMBNAIL_SIZE = 300;

    public static final FileNodeDto.MediaConfiguration currentConfig = new FileNodeDto.MediaConfiguration();

    private static final Set<String> imageExtensions = new HashSet<>(Arrays.asList("jpg", "jpeg", "png", "webp", "bmp"));
    private static final Set<String> videoExtensions = new HashSet<>(Arrays.asList("mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v"));

    private static class CacheEntry {
        long lastModified;
        ThumbInfo info;

        CacheEntry(long lastModified, ThumbInfo info) {
            this.lastModified = lastModified;
            this.info = info;
        }
    }

    /** Thumbnail result: the downscaled bytes used as the thumbnail, plus the size of the "original" source visual. */
    public static class ThumbInfo {
        public byte[] thumbBytes;
        public long originalBytes;

        public ThumbInfo() {
            this.thumbBytes = new byte[0];
            this.originalBytes = 0;
        }
    }

    private static final ConcurrentHashMap<String, CacheEntry> lowResCache = new ConcurrentHashMap<>();

    private static String getExtension(String path) {
        if (path == null) return "";
        int idx = path.lastIndexOf('.');
        if (idx < 0) return "";
        return path.substring(idx + 1).toLowerCase(Locale.ROOT);
    }

    public static boolean isImageFile(String filePath) {
        return imageExtensions.contains(getExtension(filePath));
    }

    public static boolean isVideoFile(String filePath) {
        return videoExtensions.contains(getExtension(filePath));
    }

    public static ThumbInfo getLowResBytes(String filePath, long lastModified) {
        return getLowResBytesWithOverride(filePath, lastModified, -1.0f, -1.0f);
    }

    public static ThumbInfo getLowResBytesWithOverride(String filePath, long lastModified, float frameOverride, float qualityOverride) {
        File file = new File(filePath);
        if (!file.exists() || file.length() == 0) return new ThumbInfo();

        boolean isVideo = isVideoFile(filePath);
        if (!isImageFile(filePath) && !isVideo) return new ThumbInfo();

        float effectiveFrame = (frameOverride >= 0.0f && frameOverride <= 1.0f) ? frameOverride : currentConfig.videoFrameNormalized;
        float effectiveQuality = (qualityOverride >= 0.0f && qualityOverride <= 1.0f) ? qualityOverride : currentConfig.thumbnailQuality;

        String cacheKey = filePath + "_f" + String.format(Locale.US, "%.2f", effectiveFrame) + "_q" + String.format(Locale.US, "%.2f", effectiveQuality);

        CacheEntry cached = lowResCache.get(cacheKey);
        if (cached != null && cached.lastModified == lastModified) {
            return cached.info;
        }

        ThumbInfo generated = isVideo
            ? generateVideoThumbnailInfo(filePath, effectiveFrame, effectiveQuality)
            : generateLowResImageInfo(filePath, effectiveQuality);

        if (generated != null && generated.thumbBytes != null && generated.thumbBytes.length > 0) {
            if (lowResCache.size() > 300) {
                lowResCache.clear();
            }
            lowResCache.put(cacheKey, new CacheEntry(lastModified, generated));
            return generated;
        }

        return new ThumbInfo();
    }

    public static void clearCache() {
        lowResCache.clear();
    }

    private static ThumbInfo generateLowResImageInfo(String filePath, float qualitySetting) {
        try {
            BitmapFactory.Options options = new BitmapFactory.Options();
            options.inJustDecodeBounds = true;
            BitmapFactory.decodeFile(filePath, options);

            int width = options.outWidth;
            int height = options.outHeight;
            if (width <= 0 || height <= 0) return new ThumbInfo();

            int sampleSize = 1;
            if (qualitySetting < 1.0f) {
                int targetSize = (int) (BASE_THUMBNAIL_SIZE + (qualitySetting * 1200)); // 300px up to 1500px based on qualitySetting
                while ((width / sampleSize) > targetSize || (height / sampleSize) > targetSize) {
                    sampleSize *= 2;
                }
            }

            options.inSampleSize = sampleSize;
            options.inJustDecodeBounds = false;

            Bitmap bitmap = BitmapFactory.decodeFile(filePath, options);
            if (bitmap == null) return new ThumbInfo();

            long originalSize = new File(filePath).length();
            // Quality setting maps to a 30..90 JPEG quality (0.7 ≈ 63) so lower settings
            // always produce a smaller file than the original.
            int jpegQuality = qualitySetting >= 1.0f ? 90 : Math.max(30, Math.min(90, (int) (qualitySetting * 90)));
            ByteArrayOutputStream stream = new ByteArrayOutputStream();
            bitmap.compress(Bitmap.CompressFormat.JPEG, jpegQuality, stream);
            // Safety net: a "lowered" thumbnail must never be bigger than the original file.
            while (jpegQuality > 30 && stream.size() > originalSize) {
                jpegQuality -= 20;
                stream.reset();
                bitmap.compress(Bitmap.CompressFormat.JPEG, jpegQuality, stream);
            }
            bitmap.recycle();

            ThumbInfo info = new ThumbInfo();
            info.thumbBytes = stream.toByteArray();
            info.originalBytes = originalSize;
            return info;
        } catch (Exception e) {
            SignalRClient.sendClassLog("MediaHelper", "Error", "Image downscale error for " + filePath + ": " + e.getMessage());
            return new ThumbInfo();
        }
    }

    private static ThumbInfo generateVideoThumbnailInfo(String filePath, float normFrame, float qualitySetting) {
        Bitmap rawBitmap = null;
        MediaMetadataRetriever retriever = new MediaMetadataRetriever();

        try {
            retriever.setDataSource(filePath);
            String durationStr = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION);
            long durationMs = 0;
            if (durationStr != null) {
                try { durationMs = Long.parseLong(durationStr); } catch (Exception ignored) {}
            }

            if (durationMs > 0) {
                long targetMicros = (long) (durationMs * 1000 * Math.min(1.0f, Math.max(0.0f, normFrame)));
                rawBitmap = retriever.getFrameAtTime(targetMicros, MediaMetadataRetriever.OPTION_CLOSEST_SYNC);
                if (rawBitmap == null) {
                    rawBitmap = retriever.getFrameAtTime(targetMicros);
                }
            }

            if (rawBitmap == null) {
                rawBitmap = retriever.getFrameAtTime();
            }
        } catch (Exception e) {
            SignalRClient.sendClassLog("MediaHelper", "Error", "MediaMetadataRetriever error for " + filePath + ": " + e.getMessage());
        } finally {
            try { retriever.release(); } catch (Exception ignored) {}
        }

        if (rawBitmap == null) return new ThumbInfo();

        try {
            int width = rawBitmap.getWidth();
            int height = rawBitmap.getHeight();
            if (width <= 0 || height <= 0) {
                rawBitmap.recycle();
                return new ThumbInfo();
            }

            if (qualitySetting >= 1.0f) {
                // Native: keep the full taken-frame resolution, no downscale.
                ByteArrayOutputStream nativeStream = new ByteArrayOutputStream();
                rawBitmap.compress(Bitmap.CompressFormat.JPEG, 90, nativeStream);
                byte[] nativeBytes = nativeStream.toByteArray();
                rawBitmap.recycle();
                ThumbInfo info = new ThumbInfo();
                info.thumbBytes = nativeBytes;
                info.originalBytes = nativeBytes.length;
                return info;
            }

            // Original full-res frame bytes (used for size display: first value).
            ByteArrayOutputStream originalStream = new ByteArrayOutputStream();
            rawBitmap.compress(Bitmap.CompressFormat.JPEG, 90, originalStream);
            long originalBytes = originalStream.size();

            int targetSize = (int) (BASE_THUMBNAIL_SIZE + (qualitySetting * 1200)); // Scale thumbnail target size
            float ratio = Math.min(1.0f, Math.min((float) targetSize / width, (float) targetSize / height));
            int targetWidth = Math.max(1, (int) (width * ratio));
            int targetHeight = Math.max(1, (int) (height * ratio));

            Bitmap scaledBitmap = Bitmap.createScaledBitmap(rawBitmap, targetWidth, targetHeight, true);
            rawBitmap.recycle();

            // Quality setting maps to a 30..90 JPEG quality; never bigger than the full-res original frame.
            int jpegQuality = Math.max(30, Math.min(90, (int) (qualitySetting * 90)));
            ByteArrayOutputStream stream = new ByteArrayOutputStream();
            scaledBitmap.compress(Bitmap.CompressFormat.JPEG, jpegQuality, stream);
            while (jpegQuality > 30 && stream.size() > originalBytes) {
                jpegQuality -= 20;
                stream.reset();
                scaledBitmap.compress(Bitmap.CompressFormat.JPEG, jpegQuality, stream);
            }
            scaledBitmap.recycle();

            ThumbInfo info = new ThumbInfo();
            info.thumbBytes = stream.toByteArray();
            info.originalBytes = originalBytes;
            return info;
        } catch (Exception e) {
            SignalRClient.sendClassLog("MediaHelper", "Error", "Error compressing video thumbnail for " + filePath + ": " + e.getMessage());
            return new ThumbInfo();
        }
    }
}
