#include <httplib.h>
#include <nlohmann/json.hpp>
#include <iostream>

using json = nlohmann::json;

namespace {

struct ValidationResult {
    bool approved;
    double fee;
    std::string reason;
};

ValidationResult Validate(const std::string& transactionType, double amount, double currentBalance) {
    if (amount <= 0) {
        return {false, 0.0, "Amount must be positive"};
    }

    double fee = 0.0;
    if (transactionType == "deposit") {
        fee = 0.0;
    } else if (transactionType == "withdrawal") {
        fee = amount > 500.0 ? amount * 0.01 : 0.0;
    } else if (transactionType == "transfer_out") {
        fee = 1.0;
    } else {
        return {false, 0.0, "Unknown transaction type: " + transactionType};
    }

    if (transactionType != "deposit" && amount + fee > currentBalance) {
        return {false, fee, "Insufficient funds to cover amount plus fee"};
    }

    return {true, fee, ""};
}

double Accrue(const std::string& accountType, double balance) {
    if (accountType == "savings") {
        return balance * 0.005;
    }
    return 0.0;
}

}  // namespace

int main() {
    httplib::Server svr;

    svr.Get("/health", [](const httplib::Request&, httplib::Response& res) {
        res.set_content(json{{"status", "ok"}}.dump(), "application/json");
    });

    svr.Post("/validate", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try {
            body = json::parse(req.body);
        } catch (const json::parse_error&) {
            res.status = 400;
            res.set_content(json{{"approved", false}, {"fee", 0}, {"reason", "Malformed JSON"}}.dump(),
                             "application/json");
            return;
        }

        if (!body.contains("transactionType") || !body.contains("amount") || !body.contains("currentBalance")) {
            res.status = 400;
            res.set_content(
                json{{"approved", false}, {"fee", 0}, {"reason", "Missing required fields"}}.dump(),
                "application/json");
            return;
        }

        auto result = Validate(body["transactionType"].get<std::string>(),
                                body["amount"].get<double>(),
                                body["currentBalance"].get<double>());

        res.set_content(
            json{{"approved", result.approved}, {"fee", result.fee}, {"reason", result.reason}}.dump(),
            "application/json");
    });

    svr.Post("/accrue", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try {
            body = json::parse(req.body);
        } catch (const json::parse_error&) {
            res.status = 400;
            res.set_content(json{{"interest", 0}}.dump(), "application/json");
            return;
        }

        if (!body.contains("accountType") || !body.contains("balance")) {
            res.status = 400;
            res.set_content(json{{"interest", 0}}.dump(), "application/json");
            return;
        }

        double interest = Accrue(body["accountType"].get<std::string>(), body["balance"].get<double>());
        res.set_content(json{{"interest", interest}}.dump(), "application/json");
    });

    std::cout << "Engine listening on 0.0.0.0:8081" << std::endl;
    svr.listen("0.0.0.0", 8081);
    return 0;
}
