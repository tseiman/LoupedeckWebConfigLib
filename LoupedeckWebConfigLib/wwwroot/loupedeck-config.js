(() => {
  const clone = (value) => (typeof structuredClone === "function"
    ? structuredClone(value)
    : JSON.parse(JSON.stringify(value)));

  const state = {
    config: window.LoupedeckConfigBootstrap || { actions: [], actionConfigurations: {} },
    initialActionConfigurations: {},
    providers: window.LoupedeckConfigProviders || {},
    invalidFields: new Set(),
    dirty: false
  };

  window.LoupedeckConfig = state.config;
  window.LoupedeckConfigProviders = state.providers;

  const getActionElement = (actionGuid) =>
    document.querySelector(`[data-action-guid="${actionGuid}"]`);

  const getActionFields = (actionGuid) =>
    Array.from(getActionElement(actionGuid)?.querySelectorAll("[data-config-key]") || []);

  const getActionDefinition = (actionGuid) =>
    state.config.actions.find((action) => action.actionGuid === actionGuid) || null;

  const getParameterDefinition = (actionGuid, key) => {
    const action = getActionDefinition(actionGuid);
    return action?.parameters?.find((parameter) => parameter.name === key) || null;
  };

  const getDefaultValue = (field) => {
    if (field.type === "checkbox") {
      return field.dataset.default === "true";
    }

    return field.dataset.default ?? "";
  };

  const coerceFieldValue = (field) => {
    if (field.type === "checkbox") {
      return field.checked;
    }

    const value = field.value;
    switch (field.dataset.configType || field.type) {
      case "integer":
        return value === "" ? null : Number.parseInt(value, 10);
      case "number":
        return value === "" ? null : Number(value);
      case "boolean":
        return value === "true";
      default:
        return value;
    }
  };

  const setFieldValue = (field, value) => {
    const nextValue = value ?? getDefaultValue(field);
    if (field.type === "checkbox") {
      field.checked = Boolean(nextValue);
    } else {
      field.value = nextValue;
    }
  };

  const renderSelectOptions = (field, parameter) => {
    if (!field || field.tagName !== "SELECT" || !parameter?.options) {
      return;
    }

    const currentValue = field.value;
    const options = parameter.options.map((option) => {
      if (option && typeof option === "object") {
        return {
          value: String(option.value ?? option.label ?? ""),
          label: String(option.label ?? option.value ?? "")
        };
      }

      return { value: String(option), label: String(option) };
    });

    field.innerHTML = options.map((option) => `<option value="${option.value}">${option.label}</option>`).join("");
    if (currentValue) {
      field.value = currentValue;
    }
  };

  const stableStringify = (value) => {
    if (Array.isArray(value)) {
      return `[${value.map(stableStringify).join(",")}]`;
    }

    if (value && typeof value === "object") {
      return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${stableStringify(value[key])}`).join(",")}}`;
    }

    return JSON.stringify(value);
  };

  const normalizeConfiguration = (configuration) => {
    const normalized = {};
    for (const action of state.config.actions) {
      normalized[action.actionGuid] = configuration?.[action.actionGuid] || {};
    }
    return normalized;
  };

  const updateDirtyState = () => {
    const current = {};
    for (const action of state.config.actions) {
      current[action.actionGuid] = window.collectLoupedeckActionConfig(action.actionGuid);
    }

    state.dirty = stableStringify(current) !== stableStringify(normalizeConfiguration(state.initialActionConfigurations));
    updateButtonState();
    return state.dirty;
  };

  const validateField = (field) => {
    const pattern = field.getAttribute("checkRegEx");
    let valid = true;

    if (pattern) {
      valid = new RegExp(`^(?:${pattern})$`).test(field.value);
    }

    if (valid && typeof field.checkValidity === "function") {
      valid = field.checkValidity();
    }

    field.classList.toggle("invalid", !valid);
    if (valid) {
      state.invalidFields.delete(field);
    } else {
      state.invalidFields.add(field);
    }

    updateDirtyState();
    return valid;
  };

  const updateButtonState = () => {
    const hasInvalidFields = state.invalidFields.size > 0;
    const disabled = hasInvalidFields || !state.dirty;
    document.getElementById("save-config").disabled = disabled;
    document.getElementById("save-close-config").disabled = disabled;

    if (hasInvalidFields) {
      document.getElementById("save-status").textContent = "Fix invalid fields";
    } else if (!state.dirty && document.getElementById("save-status").textContent === "Fix invalid fields") {
      document.getElementById("save-status").textContent = "";
    }
  };

  const validateAll = () => {
    for (const field of document.querySelectorAll("[data-config-key]")) {
      validateField(field);
    }

    return state.invalidFields.size === 0;
  };

  window.getLoupedeckActionConfig = (actionGuid) =>
    clone((state.config.actionConfigurations || {})[actionGuid] || null);

  window.applyLoupedeckActionConfig = (actionGuid, configuration) => {
    const config = configuration || {};
    for (const field of getActionFields(actionGuid)) {
      renderSelectOptions(field, getParameterDefinition(actionGuid, field.dataset.configKey));
      setFieldValue(field, config[field.dataset.configKey]);
      validateField(field);
    }
  };

  window.collectLoupedeckActionConfig = (actionGuid) => {
    const result = {};
    for (const field of getActionFields(actionGuid)) {
      result[field.dataset.configKey] = coerceFieldValue(field);
    }
    return result;
  };

  window.registerLoupedeckAutoConfig = (actionGuid) => {
    window.applyLoupedeckActionConfig(actionGuid, window.getLoupedeckActionConfig(actionGuid));
    state.providers[actionGuid] = () => window.collectLoupedeckActionConfig(actionGuid);

    for (const field of getActionFields(actionGuid)) {
      renderSelectOptions(field, getParameterDefinition(actionGuid, field.dataset.configKey));
      field.addEventListener("input", () => validateField(field));
      field.addEventListener("change", () => validateField(field));
      validateField(field);
    }

    updateDirtyState();
  };

  const status = () => document.getElementById("save-status");

  const collectConfiguration = async () => {
    const result = {};
    for (const action of state.config.actions) {
      const provider = state.providers[action.actionGuid] || (() => window.collectLoupedeckActionConfig(action.actionGuid));
      result[action.actionGuid] = await provider(action);
    }
    return result;
  };

  const saveConfiguration = async () => {
    if (!validateAll()) {
      status().textContent = "Fix invalid fields";
      return false;
    }

    status().textContent = "Saving...";
    const result = await collectConfiguration();
    const response = await fetch("/config", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(result)
    });

    if (!response.ok) {
      status().textContent = "Save failed";
      throw new Error(await response.text());
    }

    state.config = await response.json();
    window.LoupedeckConfig = state.config;
    state.initialActionConfigurations = clone(state.config.actionConfigurations || {});
    state.dirty = false;
    updateButtonState();
    status().textContent = "Saved";
    return true;
  };

  const resetConfiguration = () => {
    state.config.actionConfigurations = clone(state.initialActionConfigurations);
    for (const action of state.config.actions) {
      const configuration = window.getLoupedeckActionConfig(action.actionGuid);
      window.applyLoupedeckActionConfig(action.actionGuid, configuration);
      document.dispatchEvent(new CustomEvent("loupedeck-config-reset", {
        detail: { action, configuration }
      }));
    }
    state.dirty = false;
    updateButtonState();
    status().textContent = "Reset";
  };

  document.addEventListener("DOMContentLoaded", () => {
    state.initialActionConfigurations = clone(state.config.actionConfigurations || {});
    document.getElementById("reset-config").addEventListener("click", resetConfiguration);
    document.getElementById("save-config").addEventListener("click", saveConfiguration);
    document.getElementById("save-close-config").addEventListener("click", async () => {
      if (await saveConfiguration()) {
        await fetch("/close", { method: "POST" });
        window.close();
      }
    });

    for (const action of state.config.actions) {
      if (getActionFields(action.actionGuid).length > 0 && !state.providers[action.actionGuid]) {
        window.registerLoupedeckAutoConfig(action.actionGuid);
      }
    }

    validateAll();
    updateDirtyState();

    const events = new EventSource("/events");
    events.addEventListener("registration-updated", () => location.reload());
    events.addEventListener("configuration-updated", () => location.reload());
  });
})();
