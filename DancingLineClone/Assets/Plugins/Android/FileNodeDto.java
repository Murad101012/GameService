package com.UnityTechnologies.com.unity.template.urpblank;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;
import java.util.List;

public class FileNodeDto {
    public String name;
    public String relativePath;
    public boolean isDirectory;
    public long length;
    public long originalLength;
    public long originalPreviewLength;
    public String lastWriteTimeUtc;

    public FileNodeDto(String name, String relativePath, boolean isDirectory, long length, long originalLength, String lastWriteTimeUtc) {
        this.name = name;
        this.relativePath = relativePath;
        this.isDirectory = isDirectory;
        this.length = length;
        this.originalLength = originalLength;
        this.originalPreviewLength = 0;
        this.lastWriteTimeUtc = lastWriteTimeUtc;
    }

    public FileNodeDto(String name, String relativePath, boolean isDirectory, long length, long originalLength, long originalPreviewLength, String lastWriteTimeUtc) {
        this.name = name;
        this.relativePath = relativePath;
        this.isDirectory = isDirectory;
        this.length = length;
        this.originalLength = originalLength;
        this.originalPreviewLength = originalPreviewLength;
        this.lastWriteTimeUtc = lastWriteTimeUtc;
    }

    public JSONObject toJsonObject() throws JSONException {
        JSONObject json = new JSONObject();
        json.put("name", name);
        json.put("relativePath", relativePath);
        json.put("isDirectory", isDirectory);
        json.put("length", length);
        json.put("originalLength", originalLength);
        json.put("originalPreviewLength", originalPreviewLength);
        json.put("lastWriteTimeUtc", lastWriteTimeUtc);
        return json;
    }

    public static class FileNodeResultDto {
        public boolean exists;
        public FileNodeDto currentNode;
        public List<FileNodeDto> children;
        public int totalFileCount;

        public FileNodeResultDto(boolean exists, FileNodeDto currentNode, List<FileNodeDto> children) {
            this.exists = exists;
            this.currentNode = currentNode;
            this.children = children;
            this.totalFileCount = children != null ? children.size() : 0;
        }

        public FileNodeResultDto(boolean exists, FileNodeDto currentNode, List<FileNodeDto> children, int totalFileCount) {
            this.exists = exists;
            this.currentNode = currentNode;
            this.children = children;
            this.totalFileCount = totalFileCount;
        }

        public JSONObject toJsonObject() throws JSONException {
            JSONObject json = new JSONObject();
            json.put("exists", exists);
            json.put("currentNode", currentNode != null ? currentNode.toJsonObject() : new JSONObject());
            JSONArray childrenArray = new JSONArray();
            if (children != null) {
                for (FileNodeDto child : children) {
                    childrenArray.put(child.toJsonObject());
                }
            }
            json.put("children", childrenArray);
            json.put("totalFileCount", totalFileCount);
            return json;
        }
    }

    public static class ConnectionIdToPc {
        public String deviceName;
        public String deviceId;

        public ConnectionIdToPc(String deviceName, String deviceId) {
            this.deviceName = deviceName;
            this.deviceId = deviceId;
        }

        public JSONObject toJsonObject() throws JSONException {
            JSONObject json = new JSONObject();
            json.put("deviceName", deviceName);
            json.put("DeviceName", deviceName);
            json.put("deviceId", deviceId);
            json.put("DeviceId", deviceId);
            return json;
        }
    }

    public static class MediaConfiguration {
        public float videoFrameNormalized = 0.1f;
        public boolean sendDebug = true;
        public float thumbnailQuality = 0.00f;
        public float videoQuality = 0.00f;
        public float videoBitrateMultiplier = 0.50f;
        public float videoResolutionMultiplier = 1.00f;
        public boolean bitrateCappingEnabled = true;
        public int bitrateCapBps = 3_000_000;       // 3 Mbps
        public boolean resolutionCeilingEnabled = true;
        public int resolutionCeilingPx = 720;        // 720p
        public int speedLimitKbps = 0;
        public boolean keepScreenOn = false;

        // Phone parallel file-processing worker cores.
        // "Low" (2 cores) | "Medium" (half) | "High" (75%) | "Absolute" (all)
        public String parallelWork = "Medium";
    }
}
