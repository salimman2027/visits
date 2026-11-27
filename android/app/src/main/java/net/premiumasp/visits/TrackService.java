package net.premiumasp.visits;

import android.app.*;
import android.content.*;
import android.location.*;
import android.os.*;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.Locale;

/** خدمة تتبع تفضل شغالة في الخلفية (إشعار دائم) وتبعت الموقع والسرعة للسيرفر. */
public class TrackService extends Service implements LocationListener {
    static final String BASE = "https://visits.premiumasp.net/api/track";
    LocationManager lm;
    PowerManager.WakeLock wl;
    HandlerThread ht;
    Handler net;
    long lastSent = 0;
    Location lastLoc = null;

    @Override public IBinder onBind(Intent i) { return null; }

    @Override public int onStartCommand(Intent i, int f, int id) {
        String ch = "track";
        NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        if (Build.VERSION.SDK_INT >= 26) nm.createNotificationChannel(new NotificationChannel(ch, "التتبع", NotificationManager.IMPORTANCE_LOW));
        Notification.Builder nb = Build.VERSION.SDK_INT >= 26 ? new Notification.Builder(this, ch) : new Notification.Builder(this);
        PendingIntent pi = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class), PendingIntent.FLAG_IMMUTABLE);
        Notification n = nb.setContentTitle("الزيارات الميدانية").setContentText("التتبع شغال")
                .setSmallIcon(android.R.drawable.ic_menu_mylocation).setOngoing(true).setContentIntent(pi).build();
        try {
            if (Build.VERSION.SDK_INT >= 29) startForeground(1, n, android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_LOCATION);
            else startForeground(1, n);
        } catch (Exception e) { stopSelf(); return START_NOT_STICKY; }
        if (ht == null) {
            ht = new HandlerThread("trk"); ht.start(); net = new Handler(ht.getLooper());
            PowerManager pm = (PowerManager) getSystemService(POWER_SERVICE);
            wl = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "visits:trk"); wl.acquire();
            lm = (LocationManager) getSystemService(LOCATION_SERVICE);
            try {
                lm.requestLocationUpdates(LocationManager.GPS_PROVIDER, 5000, 0, this, Looper.getMainLooper());
                try { lm.requestLocationUpdates(LocationManager.NETWORK_PROVIDER, 15000, 0, this, Looper.getMainLooper()); } catch (Exception ignored) {}
            } catch (SecurityException e) { stopSelf(); }
        }
        return START_STICKY;
    }

    @Override public void onLocationChanged(Location l) {
        long now = System.currentTimeMillis();
        if (now - lastSent < 5000) return;
        if (now - lastSent < 30000 && lastLoc != null && l.distanceTo(lastLoc) < 8) return;
        double sp = l.hasSpeed() ? l.getSpeed() * 3.6 : (lastLoc != null && lastSent > 0 ? l.distanceTo(lastLoc) / ((now - lastSent) / 1000.0) * 3.6 : -1);
        lastSent = now; lastLoc = l;
        final String body = String.format(Locale.US, "{\"lat\":%.6f,\"lng\":%.6f,\"acc\":%.0f%s}", l.getLatitude(), l.getLongitude(), l.getAccuracy(),
                sp >= 0 ? String.format(Locale.US, ",\"speed\":%.1f", sp) : "");
        net.post(() -> send(body));
    }

    void send(String body) {
        String tok = getSharedPreferences("fv", MODE_PRIVATE).getString("tok", "");
        if (tok.isEmpty()) { stopSelf(); return; }
        try {
            HttpURLConnection c = (HttpURLConnection) new URL(BASE).openConnection();
            c.setRequestMethod("POST"); c.setConnectTimeout(15000); c.setReadTimeout(15000); c.setDoOutput(true);
            c.setRequestProperty("Content-Type", "application/json");
            c.setRequestProperty("Authorization", "Bearer " + tok);
            try (OutputStream o = c.getOutputStream()) { o.write(body.getBytes("UTF-8")); }
            int code = c.getResponseCode();
            if (code == 401) { getSharedPreferences("fv", MODE_PRIVATE).edit().remove("tok").apply(); stopSelf(); }
            c.disconnect();
        } catch (Exception ignored) {}
    }

    @Override public void onDestroy() {
        try { if (lm != null) lm.removeUpdates(this); } catch (Exception ignored) {}
        if (wl != null && wl.isHeld()) wl.release();
        if (ht != null) ht.quitSafely();
        super.onDestroy();
    }
}
