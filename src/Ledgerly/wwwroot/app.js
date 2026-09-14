window.ledgerly = {
    // Submits a regular (non-Blazor) form, e.g. the sign-out form posted to the server.
    submitForm: (id) => document.getElementById(id)?.submit()
};
