(() => {
  const clone = (value) => (typeof structuredClone === "function"
    ? structuredClone(value)
    : JSON.parse(JSON.stringify(value)));

  const escapeHtml = (value) => String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;");

  const state = {
    config: window.LoupedeckConfigBootstrap || { actions: [], actionConfigurations: {} },
    initialPluginConfiguration: {},
    initialActionConfigurations: {},
    pluginProviderRegistered: false,
    providers: window.LoupedeckConfigProviders || {},
    invalidFields: new Set(),
    dirty: false,
    connectionLostTimer: null
  };

  window.LoupedeckConfig = state.config;
  window.LoupedeckConfigProviders = state.providers;

  const getActionElement = (actionGuid) =>
    document.querySelector(`[data-action-guid="${actionGuid}"]`);

  const getActionFields = (actionGuid) =>
    Array.from(getActionElement(actionGuid)?.querySelectorAll("[lwcl-config-key]") || []);

  const getPluginFields = () =>
    Array.from(document.querySelector("[data-plugin-config]")?.querySelectorAll("[lwcl-config-key]") || []);

  const getActionDefinition = (actionGuid) =>
    state.config.actions.find((action) => action.actionGuid === actionGuid) || null;

  const getParameterDefinition = (actionGuid, key) => {
    const action = getActionDefinition(actionGuid);
    return action?.parameters?.find((parameter) => parameter.name === key) || null;
  };

  const getPluginParameterDefinition = (key) =>
    state.config.plugin?.parameters?.find((parameter) => parameter.name === key) || null;

  const getConfigKey = (field) =>
    field.getAttribute("lwcl-config-key");

  const getConfigType = (field) =>
    field.getAttribute("lwcl-config-type") || field.type;

  const getControlType = (field) =>
    field.getAttribute("lwcl-control") || "";

  const isMultiple = (field) =>
    field.hasAttribute("multiple");

  const getConfiguredDefault = (field) =>
    field.hasAttribute("lwcl-default") ? field.getAttribute("lwcl-default") : "";

  const getOptionLabel = (field, value) =>
    field.querySelector(`.rich-option[data-value="${CSS.escape(value)}"] .rich-option-title`)?.textContent || value;

  const normalizeOptions = (parameter, allowEmpty) => {
    const options = (parameter?.options || []).map((option) => {
      if (option && typeof option === "object") {
        return {
          value: String(option.value ?? option.label ?? ""),
          label: String(option.label ?? option.value ?? ""),
          description: String(option.description ?? "")
        };
      }

      return { value: String(option), label: String(option), description: "" };
    });

    if (allowEmpty && !options.some((option) => option.value === "")) {
      options.unshift({ value: "", label: "None", description: "" });
    }

    return options;
  };

  const updateRichSelectSummary = (field) => {
    const summary = field.querySelector(".rich-select-summary");
    if (!summary) {
      return;
    }

    const selected = Array.from(field.querySelectorAll(".rich-option.selected"))
      .map((option) => option.getAttribute("data-value") || "");
    summary.textContent = selected.length === 0
      ? "Select..."
      : selected.map((value) => getOptionLabel(field, value)).join(", ");
  };

  const closeRichSelect = (field) => {
    field.classList.remove("open");
    field.querySelector(".rich-select-summary")?.setAttribute("aria-expanded", "false");
  };

  const toggleRichSelect = (field) => {
    const open = !field.classList.contains("open");
    field.classList.toggle("open", open);
    field.querySelector(".rich-select-summary")?.setAttribute("aria-expanded", open ? "true" : "false");
  };

  const getDefaultValue = (field) => {
    if (isMultiple(field)) {
      const defaultValue = getConfiguredDefault(field);
      return defaultValue === "" ? [] : defaultValue.split(",").map((value) => value.trim()).filter(Boolean);
    }

    if (field.type === "checkbox") {
      return getConfiguredDefault(field) === "true";
    }

    return getConfiguredDefault(field);
  };

  const coerceFieldValue = (field) => {
    if (getControlType(field) === "rich-select") {
      const selected = Array.from(field.querySelectorAll(".rich-option.selected"))
        .map((option) => option.getAttribute("data-value") || "");
      return isMultiple(field) ? selected : selected[0] || "";
    }

    if (field.type === "checkbox") {
      return field.checked;
    }

    if (field.tagName === "SELECT" && isMultiple(field)) {
      return Array.from(field.selectedOptions).map((option) => option.value);
    }

    const value = field.value;
    switch (getConfigType(field)) {
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
    const nextValue = value === undefined || value === null
      ? getDefaultValue(field)
      : value;

    if (getControlType(field) === "rich-select") {
      const selected = new Set((Array.isArray(nextValue) ? nextValue : [nextValue]).map((item) => String(item)));
      for (const option of field.querySelectorAll(".rich-option")) {
        const active = selected.has(option.getAttribute("data-value") || "");
        option.classList.toggle("selected", active);
        option.setAttribute("aria-selected", active ? "true" : "false");
      }
      updateRichSelectSummary(field);
      return;
    }

    if (field.tagName === "SELECT" && isMultiple(field)) {
      const selected = new Set((Array.isArray(nextValue) ? nextValue : [nextValue]).map((item) => String(item)));
      for (const option of field.options) {
        option.selected = selected.has(option.value);
      }
      return;
    }

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

    const currentValue = isMultiple(field)
      ? Array.from(field.selectedOptions).map((option) => option.value)
      : field.value;
    const options = normalizeOptions(parameter, !isMultiple(field) && parameter.required !== true);

    field.innerHTML = options.map((option) => `<option value="${option.value}">${option.label}</option>`).join("");
    if (Array.isArray(currentValue)) {
      const selected = new Set(currentValue);
      for (const option of field.options) {
        option.selected = selected.has(option.value);
      }
    } else if (currentValue) {
      field.value = currentValue;
    }
  };

  const renderRichSelectOptions = (field, parameter) => {
    if (!field || getControlType(field) !== "rich-select" || !parameter?.options || field.dataset.rendered === "true") {
      return;
    }

    field.dataset.rendered = "true";
    field.classList.add("rich-select");
    field.style.setProperty("--rich-select-visible-options", String(Math.max(1, Number.parseInt(field.getAttribute("size") || "5", 10) || 5)));

    const optionsHtml = normalizeOptions(parameter, !isMultiple(field) && parameter.required !== true).map((option) => {
      const value = option.value;
      const label = option.label;
      const description = option.description;
      return `
        <button type="button" class="rich-option" role="option" data-value="${escapeHtml(value)}" aria-selected="false">
          <span class="rich-option-title">${escapeHtml(label)}</span>
          ${description ? `<span class="rich-option-description">${escapeHtml(description)}</span>` : ""}
        </button>`;
    }).join("");

    field.innerHTML = `
      <button type="button" class="rich-select-summary" aria-haspopup="listbox" aria-expanded="false">Select...</button>
      <div class="rich-select-menu" role="listbox" aria-multiselectable="${isMultiple(field) ? "true" : "false"}">
        ${optionsHtml}
      </div>`;

    field.querySelector(".rich-select-summary").addEventListener("click", () => toggleRichSelect(field));

    field.addEventListener("click", (event) => {
      const option = event.target.closest(".rich-option");
      if (!option || !field.contains(option)) {
        return;
      }

      if (!isMultiple(field)) {
        for (const item of field.querySelectorAll(".rich-option")) {
          item.classList.remove("selected");
          item.setAttribute("aria-selected", "false");
        }
      }

      const active = isMultiple(field) ? !option.classList.contains("selected") : true;
      option.classList.toggle("selected", active);
      option.setAttribute("aria-selected", active ? "true" : "false");
      updateRichSelectSummary(field);
      if (!isMultiple(field)) {
        closeRichSelect(field);
      }
      field.dispatchEvent(new Event("change", { bubbles: true }));
    });

    document.addEventListener("click", (event) => {
      if (!field.contains(event.target)) {
        closeRichSelect(field);
      }
    });
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
    const current = {
      plugin: window.collectLoupedeckPluginConfig(),
      actions: {}
    };
    for (const action of state.config.actions) {
      current.actions[action.actionGuid] = window.collectLoupedeckActionConfig(action.actionGuid);
    }

    const initial = {
      plugin: state.initialPluginConfiguration || {},
      actions: normalizeConfiguration(state.initialActionConfigurations)
    };
    state.dirty = stableStringify(current) !== stableStringify(initial);
    updateButtonState();
    return state.dirty;
  };

  const validateField = (field) => {
    const pattern = field.getAttribute("lwcl-check-regex");
    let valid = true;

    if (pattern && !isMultiple(field)) {
      try {
        valid = new RegExp(`^(?:${pattern})$`).test(String(coerceFieldValue(field)));
      } catch {
        valid = false;
      }
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

  const attachFieldHandlers = (field) => {
    field.addEventListener("input", () => validateField(field));
    field.addEventListener("change", () => validateField(field));
    validateField(field);
  };

  const updateButtonState = () => {
    const saveButton = document.getElementById("save-config");
    const saveCloseButton = document.getElementById("save-close-config");
    const saveStatus = document.getElementById("save-status");
    if (!saveButton || !saveCloseButton || !saveStatus) {
      return;
    }

    const hasInvalidFields = state.invalidFields.size > 0;
    const disabled = hasInvalidFields || !state.dirty;
    saveButton.disabled = disabled;
    saveCloseButton.disabled = disabled;

    if (hasInvalidFields) {
      saveStatus.textContent = "Fix invalid fields";
    } else if (!state.dirty && saveStatus.textContent === "Fix invalid fields") {
      saveStatus.textContent = "";
    }
  };

  const validateAll = () => {
    for (const field of document.querySelectorAll("[lwcl-config-key]")) {
      validateField(field);
    }

    return state.invalidFields.size === 0;
  };

  window.getLoupedeckActionConfig = (actionGuid) =>
    clone((state.config.actionConfigurations || {})[actionGuid] || null);

  window.getLoupedeckPluginConfig = () =>
    clone(state.config.pluginConfiguration || null);

  window.applyLoupedeckPluginConfig = (configuration) => {
    const config = configuration || {};
    for (const field of getPluginFields()) {
      const key = getConfigKey(field);
      const parameter = getPluginParameterDefinition(key);
      renderSelectOptions(field, parameter);
      renderRichSelectOptions(field, parameter);
      setFieldValue(field, config[key]);
      validateField(field);
    }
  };

  window.applyLoupedeckActionConfig = (actionGuid, configuration) => {
    const config = configuration || {};
    for (const field of getActionFields(actionGuid)) {
      const key = getConfigKey(field);
      const parameter = getParameterDefinition(actionGuid, key);
      renderSelectOptions(field, parameter);
      renderRichSelectOptions(field, parameter);
      setFieldValue(field, config[key]);
      validateField(field);
    }
  };

  window.collectLoupedeckActionConfig = (actionGuid) => {
    const result = {};
    for (const field of getActionFields(actionGuid)) {
      result[getConfigKey(field)] = coerceFieldValue(field);
    }
    return result;
  };

  window.collectLoupedeckPluginConfig = () => {
    const result = {};
    for (const field of getPluginFields()) {
      result[getConfigKey(field)] = coerceFieldValue(field);
    }
    return result;
  };

  window.registerLoupedeckPluginAutoConfig = () => {
    window.applyLoupedeckPluginConfig(window.getLoupedeckPluginConfig());

    for (const field of getPluginFields()) {
      if (field.dataset.lwclBound === "true") {
        continue;
      }

      const parameter = getPluginParameterDefinition(getConfigKey(field));
      renderSelectOptions(field, parameter);
      renderRichSelectOptions(field, parameter);
      attachFieldHandlers(field);
      field.dataset.lwclBound = "true";
    }

    state.pluginProviderRegistered = true;
    updateDirtyState();
  };

  window.registerLoupedeckAutoConfig = (actionGuid) => {
    window.applyLoupedeckActionConfig(actionGuid, window.getLoupedeckActionConfig(actionGuid));
    state.providers[actionGuid] = () => window.collectLoupedeckActionConfig(actionGuid);

    for (const field of getActionFields(actionGuid)) {
      if (field.dataset.lwclBound === "true") {
        continue;
      }

      const parameter = getParameterDefinition(actionGuid, getConfigKey(field));
      renderSelectOptions(field, parameter);
      renderRichSelectOptions(field, parameter);
      attachFieldHandlers(field);
      field.dataset.lwclBound = "true";
    }

    updateDirtyState();
  };

  const status = () => document.getElementById("save-status");

  const collectConfiguration = async () => {
    const result = {
      plugin: window.collectLoupedeckPluginConfig(),
      actions: {}
    };
    for (const action of state.config.actions) {
      const provider = state.providers[action.actionGuid] || (() => window.collectLoupedeckActionConfig(action.actionGuid));
      result.actions[action.actionGuid] = await provider(action);
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
    state.initialPluginConfiguration = clone(state.config.pluginConfiguration || {});
    state.initialActionConfigurations = clone(state.config.actionConfigurations || {});
    state.dirty = false;
    updateButtonState();
    status().textContent = "Saved";
    return true;
  };

  const resetConfiguration = () => {
    state.config.pluginConfiguration = clone(state.initialPluginConfiguration);
    state.config.actionConfigurations = clone(state.initialActionConfigurations);
    window.applyLoupedeckPluginConfig(window.getLoupedeckPluginConfig());
    document.dispatchEvent(new CustomEvent("loupedeck-plugin-config-reset", {
      detail: { plugin: state.config.plugin, configuration: window.getLoupedeckPluginConfig() }
    }));
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
    state.initialPluginConfiguration = clone(state.config.pluginConfiguration || {});
    state.initialActionConfigurations = clone(state.config.actionConfigurations || {});
    document.getElementById("reset-config").addEventListener("click", resetConfiguration);
    document.getElementById("save-config").addEventListener("click", saveConfiguration);
    document.getElementById("save-close-config").addEventListener("click", async () => {
      if (await saveConfiguration()) {
        await fetch("/close", { method: "POST" });
        window.close();
      }
    });

    if (getPluginFields().length > 0 && !state.pluginProviderRegistered) {
      window.registerLoupedeckPluginAutoConfig();
    }

    for (const action of state.config.actions) {
      if (getActionFields(action.actionGuid).length > 0 && !state.providers[action.actionGuid]) {
        window.registerLoupedeckAutoConfig(action.actionGuid);
      }
    }

    validateAll();
    updateDirtyState();

    const events = new EventSource("/events");
    events.addEventListener("open", () => {
      if (state.connectionLostTimer) {
        clearTimeout(state.connectionLostTimer);
        state.connectionLostTimer = null;
      }
    });
    events.addEventListener("registration-updated", () => location.reload());
    events.addEventListener("configuration-updated", () => location.reload());
    events.addEventListener("error", () => {
      if (state.connectionLostTimer) {
        return;
      }

      state.connectionLostTimer = setTimeout(() => {
        if (events.readyState === EventSource.OPEN) {
          state.connectionLostTimer = null;
          return;
        }

        document.getElementById("save-config").disabled = true;
        document.getElementById("save-close-config").disabled = true;
        status().textContent = "Configuration closed. Reopen from Loupedeck.";
      }, 2000);
    });
  });
})();
