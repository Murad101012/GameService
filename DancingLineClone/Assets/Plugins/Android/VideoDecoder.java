package com.UnityTechnologies.com.unity.template.urpblank;

import android.app.ActivityManager;
import android.content.Context;
import android.media.MediaMetadataRetriever;
import android.net.Uri;
import android.os.Environment;
import android.os.Handler;
import android.os.Looper;
import android.os.StatFs;
import android.util.Log;

import androidx.media3.common.MediaItem;
import androidx.media3.common.MimeTypes;
import androidx.media3.effect.Presentation;
import androidx.media3.transformer.Composition;
import androidx.media3.transformer.DefaultEncoderFactory;
import androidx.media3.transformer.EditedMediaItem;
import androidx.media3.transformer.Effects;
import androidx.media3.transformer.ExportException;
import androidx.media3.transformer.ExportResult;
import androidx.media3.transformer.ProgressHolder;
import androidx.media3.transformer.Transformer;
import androidx.media3.transformer.VideoEncoderSettings;

import java.io.File;
import java.util.Collections;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

public class VideoDecoder {

    private static final String TAG = "VideoDecoder";
    private static final long MIN_FREE_BYTES = 1024L * 1024 * 1024; // 1 GB
    private static final long MIN_AVAILABLE_MEM_MB = 500;            // 500 MB

    private final Context appContext;
    private final String inputPath;
    private final String outputPath;
    private final float bitrateMultiplier;
    private final float resolutionMultiplier;
    private final Boolean bitrateCappingOverride;
    private final Integer bitrateCapBpsOverride;
    private final Boolean resolutionCeilingOverride;
    private final Integer resolutionCeilingPxOverride;
    private final TranscodeCallback callback;

    private final AtomicBoolean cancelled = new AtomicBoolean(false);
    private final AtomicInteger progressPercent = new AtomicInteger(0);
    private final AtomicLong outputBytes = new AtomicLong(0);

    private Transformer transformer;
    private Handler progressHandler;
    private Runnable progressRunnable;
    private int lastDebugPercent = -1;

    // ── Debug logging bridge ──────────────────────────────────────────
    private static boolean debugEnabled = false;
    private static DebugLogger debugLogger = null;

    /** Set by SignalRClient to route debug messages to PC. */
    public static void setDebugLogger(boolean enabled, DebugLogger logger) {
        debugEnabled = enabled;
        debugLogger = logger;
    }

    public interface DebugLogger {
        void log(String tag, String message);
    }

    private static void debug(String msg) {
        Log.d(TAG, msg);
        if (debugEnabled && debugLogger != null) debugLogger.log(TAG, msg);
    }

    public interface TranscodeCallback {
        void onProgress(int percent, long outputBytesSoFar);
        void onComplete(String outputPath, long totalBytes, int outputWidth, int outputHeight);
        void onWarning(String message);
        void onError(String message);
    }

    public VideoDecoder(Context ctx, String inputPath, String outputPath,
                        float bitrateMultiplier, float resolutionMultiplier,
                        TranscodeCallback callback) {
        this(ctx, inputPath, outputPath, bitrateMultiplier, resolutionMultiplier,
            null, null, null, null, callback);
    }

    /** @param bitrateCappingOverride / etc. per-video overrides; null means use the device config. */
    public VideoDecoder(Context ctx, String inputPath, String outputPath,
                        float bitrateMultiplier, float resolutionMultiplier,
                        Boolean bitrateCappingOverride, Integer bitrateCapBpsOverride,
                        Boolean resolutionCeilingOverride, Integer resolutionCeilingPxOverride,
                        TranscodeCallback callback) {
        this.appContext = (ctx != null ? ctx.getApplicationContext() : null);
        this.inputPath = inputPath;
        this.outputPath = outputPath;
        this.bitrateMultiplier = clamp(bitrateMultiplier, 0.05f, 1.0f);
        this.resolutionMultiplier = clamp(resolutionMultiplier, 0.05f, 1.0f);
        this.bitrateCappingOverride = bitrateCappingOverride;
        this.bitrateCapBpsOverride = bitrateCapBpsOverride;
        this.resolutionCeilingOverride = resolutionCeilingOverride;
        this.resolutionCeilingPxOverride = resolutionCeilingPxOverride;
        this.callback = callback;
    }

    public String getOutputPath() { return outputPath; }
    public int getProgress() { return progressPercent.get(); }
    public long getOutputBytes() { return outputBytes.get(); }

    public void start() {
        new Thread(this::runPipeline, "VideoDecoderThread").start();
    }

    public void cancel() {
        cancelled.set(true);
        stopProgressPolling();
        if (transformer != null) {
            new Handler(Looper.getMainLooper()).post(() -> {
                try { transformer.cancel(); } catch (Exception ignored) {}
            });
        }
        try { new File(outputPath).delete(); } catch (Exception ignored) {}
    }

    private boolean hasEnoughResources() {
        boolean ramOk = true;
        boolean diskOk = true;

        if (appContext != null) {
            ActivityManager am = (ActivityManager) appContext.getSystemService(Context.ACTIVITY_SERVICE);
            if (am != null) {
                ActivityManager.MemoryInfo mi = new ActivityManager.MemoryInfo();
                am.getMemoryInfo(mi);
                long availMemMB = mi.availMem / (1024 * 1024);
                ramOk = availMemMB >= MIN_AVAILABLE_MEM_MB;
                debug("RAM: " + availMemMB + " MB available (need " + MIN_AVAILABLE_MEM_MB + " MB)");
            }
        }

        File dir = new File(outputPath).getParentFile();
        if (dir == null) dir = Environment.getExternalStorageDirectory();
        try {
            StatFs stat = new StatFs(dir.getAbsolutePath());
            long freeBytes = stat.getAvailableBlocksLong() * stat.getBlockSizeLong();
            diskOk = freeBytes >= MIN_FREE_BYTES;
            debug("Storage: " + (freeBytes / (1024*1024)) + " MB available (need " + (MIN_FREE_BYTES / (1024*1024)) + " MB)");
        } catch (Exception e) {
            diskOk = false;
        }

        if (!ramOk && !diskOk) {
            if (callback != null) {
                callback.onError("Insufficient resources: RAM < " + MIN_AVAILABLE_MEM_MB 
                    + " MB AND storage < " + (MIN_FREE_BYTES / (1024*1024)) + " MB");
            }
            return false;
        }

        if (!ramOk && callback != null) callback.onWarning("RAM low (< " + MIN_AVAILABLE_MEM_MB + " MB), but proceeding.");
        if (!diskOk && callback != null) callback.onWarning("Storage low (< " + (MIN_FREE_BYTES / (1024*1024)) + " MB), but proceeding.");
        return true;
    }

    private void runPipeline() {
        debug("START input=" + inputPath + " out=" + outputPath
            + " brm=" + String.format("%.2f", bitrateMultiplier)
            + " resm=" + String.format("%.2f", resolutionMultiplier));

        File inFile = new File(inputPath);
        if (!inFile.exists()) {
            if (callback != null) callback.onError("Input file not found: " + inputPath);
            return;
        }

        if (!hasEnoughResources()) return;

        // 1. Extract metadata (bitrate, original dimensions & rotation)
        int originalBitrate = 5_000_000;
        int origWidth = 1920;
        int origHeight = 1080;
        int videoRotation = 0;
        try {
            MediaMetadataRetriever retriever = new MediaMetadataRetriever();
            retriever.setDataSource(inputPath);
            
            String bitrateStr = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_BITRATE);
            if (bitrateStr != null) originalBitrate = Integer.parseInt(bitrateStr);
            
            String widthStr = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_WIDTH);
            if (widthStr != null) origWidth = Integer.parseInt(widthStr);
            
            String heightStr = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_HEIGHT);
            if (heightStr != null) origHeight = Integer.parseInt(heightStr);

            String rotStr = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_ROTATION);
            if (rotStr != null) videoRotation = Integer.parseInt(rotStr);
            
            retriever.release();
        } catch (Exception ignored) {}

        // SWAP width and height if the video is rotated 90 or 270 degrees (e.g., horizontal video shot on phone)
        if (videoRotation == 90 || videoRotation == 270) {
            int tempDim = origWidth;
            origWidth = origHeight;
            origHeight = tempDim;
        }

        // Calculate target dimensions and round down to even numbers (H.264 requirement)
        int targetWidth = (int) (origWidth * resolutionMultiplier);
        int targetHeight = (int) (origHeight * resolutionMultiplier);
        if (targetWidth % 2 != 0) targetWidth--;
        if (targetHeight % 2 != 0) targetHeight--;
        targetWidth = Math.max(16, targetWidth);
        targetHeight = Math.max(16, targetHeight);

        final int finalWidth = targetWidth;
        final int finalHeight = targetHeight;
        final int targetBitrate = Math.max(300_000, (int) (originalBitrate * bitrateMultiplier));

        // ── Bitrate: never exceed the source video's native bitrate. If the PC asks for
        //    more than the RAW bitrate (slider or hard cap), fall back to the raw bitrate.
        int tempBitrate = Math.max(300_000, (int) (originalBitrate * bitrateMultiplier));
        final boolean bitrateNativeClamped = tempBitrate > originalBitrate;
        if (bitrateNativeClamped) tempBitrate = originalBitrate;
        boolean bitrateCappingEnabled = bitrateCappingOverride != null ? bitrateCappingOverride : MediaHelper.currentConfig.bitrateCappingEnabled;
        int bitrateCapBps = bitrateCapBpsOverride != null ? bitrateCapBpsOverride : MediaHelper.currentConfig.bitrateCapBps;
        if (bitrateCappingEnabled && tempBitrate > bitrateCapBps) {
            tempBitrate = bitrateCapBps;
        }
        final int cappedBitrate = tempBitrate;

        // ── Resolution: never exceed the source video's native resolution. This is
        //    checked independently of bitrate — one may clamp while the other stays.
        int tempW = targetWidth;
        int tempH = targetHeight;
        final boolean resNativeClamped = tempW > origWidth || tempH > origHeight;
        if (tempW > origWidth) tempW = origWidth;
        if (tempH > origHeight) tempH = origHeight;
        if (tempW % 2 != 0) tempW--;
        if (tempH % 2 != 0) tempH--;
        tempW = Math.max(16, tempW);
        tempH = Math.max(16, tempH);
        boolean resolutionCeilingEnabled = resolutionCeilingOverride != null ? resolutionCeilingOverride : MediaHelper.currentConfig.resolutionCeilingEnabled;
        int resolutionCeilingPx = resolutionCeilingPxOverride != null ? resolutionCeilingPxOverride : MediaHelper.currentConfig.resolutionCeilingPx;
        if (resolutionCeilingEnabled) {
            int ceil = resolutionCeilingPx;
            int maxDim = Math.max(tempW, tempH);
            if (maxDim > ceil) {
                float scaleDown = (float) ceil / maxDim;
                tempW = (int) (tempW * scaleDown);
                tempH = (int) (tempH * scaleDown);
                if (tempW % 2 != 0) tempW--;
                if (tempH % 2 != 0) tempH--;
                tempW = Math.max(16, tempW);
                tempH = Math.max(16, tempH);
            }
        }
        final int cappedWidth = tempW;
        final int cappedHeight = tempH;

        debug("Video: " + origWidth + "x" + origHeight + " " + (originalBitrate/1000) + "kbps"
            + " -> " + cappedWidth + "x" + cappedHeight + " " + (cappedBitrate/1000) + "kbps"
            + (bitrateNativeClamped ? " [bitrate native]" : (cappedBitrate != targetBitrate ? " [bitrate cap]" : ""))
            + (resNativeClamped ? " [res native]" : ((cappedWidth != finalWidth || cappedHeight != finalHeight) ? " [res ceiling]" : "")));

        // 2. Execute Media3 Transformer on Main Thread
        new Handler(Looper.getMainLooper()).post(() -> {
            try {
                Uri inputUri = Uri.fromFile(inFile);

                // Use Presentation effect to physically resize video stream canvas
                Presentation presentationEffect = Presentation.createForWidthAndHeight(
                        cappedWidth, cappedHeight, Presentation.LAYOUT_SCALE_TO_FIT
                );

                EditedMediaItem editedMediaItem = new EditedMediaItem.Builder(MediaItem.fromUri(inputUri))
                        .setEffects(new Effects(
                                Collections.emptyList(),
                                Collections.singletonList(presentationEffect)
                        ))
                        .build();

                DefaultEncoderFactory encoderFactory = new DefaultEncoderFactory.Builder(appContext)
                        .setRequestedVideoEncoderSettings(
                                new VideoEncoderSettings.Builder()
                                        .setBitrate(cappedBitrate)
                                        .build()
                        )
                        .build();

                transformer = new Transformer.Builder(appContext)
                        .setVideoMimeType(MimeTypes.VIDEO_H264)
                        .setEncoderFactory(encoderFactory)
                        .addListener(new Transformer.Listener() {
                            @Override
                            public void onCompleted(Composition composition, ExportResult exportResult) {
                                stopProgressPolling();
                                progressPercent.set(100);
                                long outSize = new File(outputPath).length();
                                outputBytes.set(outSize);
                                debug("COMPLETE: " + outputPath + " " + outSize + " bytes " + cappedWidth + "x" + cappedHeight);
                                if (callback != null) {
                                    callback.onComplete(outputPath, outSize, cappedWidth, cappedHeight);
                                }
                            }

                            @Override
                            public void onError(Composition composition, ExportResult exportResult, ExportException exception) {
                                stopProgressPolling();
                                progressPercent.set(-1);
                                debug("ERROR: " + exception.getClass().getSimpleName() + " - " + exception.getMessage()
                                    + (exception.getCause() != null ? " cause=" + exception.getCause().getMessage() : ""));
                                Log.e(TAG, "Media3 Exception: " + exception.getMessage(), exception);
                                if (callback != null) {
                                    callback.onError(exception.getMessage());
                                }
                            }
                        })
                        .build();

                File outFile = new File(outputPath);
                if (outFile.exists()) outFile.delete();

                debug("Transformer start: uri=" + inputUri + " presentation=" + cappedWidth + "x" + cappedHeight);
                transformer.start(editedMediaItem, outputPath);
                startProgressPolling();

            } catch (Exception e) {
                debug("CATCH: " + e.getClass().getSimpleName() + " - " + e.getMessage()
                    + (e.getCause() != null ? " cause=" + e.getCause().getMessage() : ""));
                Log.e(TAG, "Media3 start exception", e);
                if (callback != null) callback.onError("Failed to initialize encoder: " + e.getMessage());
            }
        });
    }

    private void startProgressPolling() {
        progressHandler = new Handler(Looper.getMainLooper());
        ProgressHolder progressHolder = new ProgressHolder();

        progressRunnable = new Runnable() {
            @Override
            public void run() {
                if (cancelled.get() || transformer == null) return;

                int state = transformer.getProgress(progressHolder);
                if (state != Transformer.PROGRESS_STATE_NOT_STARTED) {
                    int pct = progressHolder.progress;
                    progressPercent.set(pct);
                    long currentBytes = new File(outputPath).length();
                    outputBytes.set(currentBytes);

                    if (pct >= lastDebugPercent + 10) {
                        lastDebugPercent = pct;
                        debug("Progress: " + pct + "% " + currentBytes + " bytes");
                    }

                    if (callback != null) {
                        callback.onProgress(pct, currentBytes);
                    }
                }

                if (progressPercent.get() < 100 && progressPercent.get() >= 0 && !cancelled.get()) {
                    progressHandler.postDelayed(this, 500);
                }
            }
        };

        progressHandler.post(progressRunnable);
    }

    private void stopProgressPolling() {
        if (progressHandler != null && progressRunnable != null) {
            progressHandler.removeCallbacks(progressRunnable);
        }
    }

    private static float clamp(float value, float min, float max) {
        return Math.max(min, Math.min(max, value));
    }
}