// Event-based activation: runs without any UI when a message is composed or its recipients change.
import { applySignature } from './core';

async function handle(event: Office.AddinCommands.Event) {
  try {
    await applySignature();
  } catch {
    // Never block composing. In combo mode the server-side pass still stamps the message after sending.
  } finally {
    event.completed();
  }
}

Office.onReady(() => {
  // Names must match the FunctionName attributes of the LaunchEvent entries in the manifest.
  Office.actions.associate('onNewMessageComposeHandler', handle);
  Office.actions.associate('onMessageRecipientsChangedHandler', handle);
  Office.actions.associate('onNewAppointmentOrganizerHandler', handle);
});
