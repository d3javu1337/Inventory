package com.assetinventorymobile

import android.app.Activity
import android.content.Intent
import android.util.Base64
import com.facebook.react.bridge.BaseActivityEventListener
import com.facebook.react.bridge.Promise
import com.facebook.react.bridge.ReactApplicationContext
import com.facebook.react.bridge.ReactContextBaseJavaModule
import com.facebook.react.bridge.ReactMethod
import com.facebook.react.bridge.UiThreadUtil
import java.util.concurrent.Executors

class ReportFilesModule(private val context: ReactApplicationContext) : ReactContextBaseJavaModule(context) {
  private val executor = Executors.newSingleThreadExecutor()
  private var pending: Promise? = null
  private var contents: String? = null

  private val listener = object : BaseActivityEventListener() {
    override fun onActivityResult(activity: Activity, requestCode: Int, resultCode: Int, data: Intent?) {
      if (requestCode != REQUEST_SAVE) return
      val promise = pending ?: return
      val encoded = contents
      pending = null
      contents = null
      val uri = data?.data
      if (resultCode != Activity.RESULT_OK || uri == null || encoded == null) {
        promise.resolve(false)
        return
      }
      executor.execute {
        try {
          val bytes = Base64.decode(encoded, Base64.DEFAULT)
          val stream = context.contentResolver.openOutputStream(uri, "wt")
            ?: throw IllegalStateException("No output stream")
          stream.use { it.write(bytes) }
          promise.resolve(true)
        } catch (_: Exception) {
          promise.reject("REPORT_SAVE_FAILED", "Не удалось сохранить отчёт в выбранном месте. Повторите попытку.")
        }
      }
    }
  }

  init { context.addActivityEventListener(listener) }
  override fun getName() = "ReportFiles"

  @ReactMethod
  fun save(fileName: String, mimeType: String, base64: String, promise: Promise) {
    if (mimeType !in setOf("application/pdf", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")) {
      promise.reject("REPORT_FORMAT", "Неподдерживаемый формат отчёта.")
      return
    }
    UiThreadUtil.runOnUiThread {
      val activity = context.currentActivity
      if (activity == null) {
        promise.reject("REPORT_NO_ACTIVITY", "Откройте приложение и повторите сохранение отчёта.")
      } else if (pending != null) {
        promise.reject("REPORT_BUSY", "Сначала завершите сохранение предыдущего отчёта.")
      } else {
        pending = promise
        contents = base64
        val safeName = fileName.substringAfterLast('/').substringAfterLast('\\').take(200)
        val intent = Intent(Intent.ACTION_CREATE_DOCUMENT).apply {
          addCategory(Intent.CATEGORY_OPENABLE)
          type = mimeType
          putExtra(Intent.EXTRA_TITLE, safeName)
        }
        try { activity.startActivityForResult(intent, REQUEST_SAVE) }
        catch (_: Exception) {
          pending = null
          contents = null
          promise.reject("REPORT_NO_PICKER", "Не удалось открыть системный диалог сохранения.")
        }
      }
    }
  }

  override fun invalidate() {
    context.removeActivityEventListener(listener)
    UiThreadUtil.runOnUiThread {
      pending?.reject("REPORT_CLOSED", "Сохранение отчёта прервано закрытием приложения.")
      pending = null
      contents = null
      executor.shutdown()
    }
    super.invalidate()
  }

  companion object { private const val REQUEST_SAVE = 48112 }
}
